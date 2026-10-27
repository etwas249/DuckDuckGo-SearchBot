using System.Net;
using System.Net.Http.Headers;
using Newtonsoft.Json.Linq;
using HtmlAgilityPack;
using Esprima;
using Esprima.Ast;
using Esprima.Utils;

using DuckDuckGo.SearchBot.Configuration;

namespace DuckDuckGo.SearchBot.Engine;

public class SearchApi {
	private const string SearchBaseUrl = "https://duckduckgo.com/?q=";

	// Один клиент на все экземпляры
	private static HttpClient s_client = new HttpClient();

	// Первый запрос получает документ Html, содержащий ссылку с параметрами
	private Request _forParameterizedUri = new Request(s_client) {
			Version = HttpVersion.Version20,
			Headers = Headers.ParameterizedLink
	};

	// Второй запрос получает код Javascript, содержащий результаты поиска
	private Request _forJavascriptResults = new Request(s_client) {
			Headers = Headers.JavascriptResults
	};

	// Извлечение ссылки с параметрами из первого запроса
	private async Task<string> GetParameterizedUri(string text, Dictionary<string, string> headers) {
		var response = await _forParameterizedUri.CreateAndSendAsync(SearchBaseUrl + text, headers);

		var parameterizedUri = OutputManager.ParseParameterizedUri(response);

		return parameterizedUri;
	}

	// Извлечение результатов из второго запроса
	private async Task<(List<Result> results, string nextPageUri)> GetResults(string uri, Dictionary<string, string> headers) {
		var response = await _forJavascriptResults.CreateAndSendAsync(uri, headers);

		List<Result> results = new List<Result>(); 

		string nextPageUri = "";

		try {
			IEnumerable<JToken> tokens = OutputManager.ParseTokens(response);

			results = OutputManager.ParseResults(tokens, out nextPageUri);
		}
		catch (AccessDeniedError) {
			results.Add(
				new Result() { 
					Title = "Ошибка сервера",
					Description = "Сервер временно заблокировал доступ из-за перегрузки...",
					Url = "" 
				}
			);
		}
		catch (DataParseError) {
			results.Add(
				new Result() { 
					Title = "Ошибка извлечения данных",
					Description = "Не удалось корректно извлечь данные...",
					Url = "" 
				}
			);
		}
		catch (Exception e) {
			results.Add(
				new Result() { 
					Title = "Непредвиденная ошибка",
					Description = $"{e.InnerException}",
					Url = "" 
				}
			);
		}

		return (results, nextPageUri);
	}

	// Комбинирование первого и второго извлечений
	public async IAsyncEnumerable<List<Result>> Search(string text) {
		// Каждый отдельный поиск осуществляется со случайными заголовками клиента
		var clientHeaders = Headers.Client;

		var parameterizedUri = await GetParameterizedUri(text, clientHeaders);
		
		string nextPageUri = parameterizedUri;
		while (!string.IsNullOrEmpty(nextPageUri)) {
			var results = await GetResults(nextPageUri, clientHeaders);
			
			// Если null, цикл прекращает работу
			nextPageUri = results.nextPageUri;
			
			// Если nextPageUri == null, в результате будет сообщение об ошибке
			yield return results.results;
		}

		yield break;
	}
}

public record struct Result {
	private const string IconBaseUrl = "https://external-content.duckduckgo.com/ip3/{0}.ico";

	// Пары ключей "t", "a" и "u" соответствуют ключам в результатах Javascript
	internal Dictionary<string, string> ParsedKeys { readonly get; init; }

	public string Title { get => ParsedKeys["t"]; init => ParsedKeys["t"] = value; }
	public string Description { get => ParsedKeys["a"]; init => ParsedKeys["a"] = value; }
	public string Url { get => ParsedKeys["u"]; init => ParsedKeys["u"] = value; }
	public string Icon { get => GetIconPath(); }

	public Result() {
		ParsedKeys = new Dictionary<string, string> {
			["t"] = "",
			["a"] = "",
			["u"] = "",
		};
	}

	// Оборачивание ссылки в инструмент от DuckDuckGo (в IconBaseUrl)
	private string GetIconPath() {
		if (!string.IsNullOrEmpty(Url)) {
			var host = new Uri(Url).Host;

			return string.Format(IconBaseUrl, host);
		}

		return "";
	}

	// Для предпросмотра
	public static string GetCleanText(string text) {
	    var document = new HtmlDocument();
	    document.LoadHtml(text);

	    return document.DocumentNode.InnerText;
	}
}

internal class Request(HttpClient client) {
	// Чтобы запросы отправлялись последовательно не перегружая сервер
	private static readonly SemaphoreSlim s_semaphore = new SemaphoreSlim(1, 1);
	
	private HttpClient _client = client;

	// Опционально в запросах
	public Version Version { 
		get => field ?? HttpVersion.Version11;
		init { field = value; } 
	}
	public Dictionary<string, string>? Headers { get; set; }

	public async Task<string> CreateAndSendAsync(string uri, Dictionary<string, string> clientHeaders) {
		await s_semaphore.WaitAsync();
		
		try {
			using var request = new HttpRequestMessage(HttpMethod.Get, uri) {
				Version = Version,
				VersionPolicy = HttpVersionPolicy.RequestVersionExact
			};

			request.Headers.AddUsingDictionary(clientHeaders);
			
			if (Headers is not null) {
				request.Headers.AddUsingDictionary(Headers);
			}

			using var response = await _client.SendAsync(request);

			response.EnsureSuccessStatusCode();

			return await response.Content.ReadAsStringAsync();
		}
		finally {
			s_semaphore.Release();
		}
	}
}

internal static class Headers {
	private static readonly JObject s_json = Config.AppSettings;
	private static readonly JArray s_clients = ConvertToJArray("headers.clients");

	// Каждый раз при обращении будут выбраны случайные заголовки клиента
	public static Dictionary<string, string> Client { get => ConvertToDictionary("headers.clients" + GetRandomClient()); }
	public static Dictionary<string, string> ParameterizedLink { get => ConvertToDictionary("headers.html"); }
	public static Dictionary<string, string> JavascriptResults { get => ConvertToDictionary("headers.javascript"); }

	private static Dictionary<string, string> ConvertToDictionary(string path) {
		var jToken = s_json.SelectToken(path);

		return jToken?.ToObject<Dictionary<string, string>>()!;
	}

	private static JArray ConvertToJArray(string path) {
		var jToken = s_json.SelectToken(path);
		
		return JArray.Parse(jToken?.ToString()!);
	}

	private static string GetRandomClient() {
		return s_clients[Random.Shared.Next(s_clients.Count)].Path;
	}
}

internal static class OutputManager {
	private const string NewPageBaseUrl = "https://links.duckduckgo.com/{0}&biaexp=b&desktopadclickablecontentexp=b&discussionsciexp=b&litexp=b&msvrtexp=b&searchbarexp=b&weatherexp=b&wrap=1";

	private static readonly JavaScriptParser javaScriptParser = new JavaScriptParser();

	public static string ParseParameterizedUri(string content) {
		var document = new HtmlDocument();
		document.LoadHtml(content);

		var element = document.GetElementbyId("deep_preload_script") 
			?? throw new DataParseError("Ссылка с параметрами не найдена.");

		string parameterizedUri = element.GetAttributeValue("src", "");

		return parameterizedUri;
	}

	public static IEnumerable<JToken> ParseTokens(string content) {
		// Перевод Javascript в Json для парсинга
		Script contentToScript = javaScriptParser.ParseScript(content);

		JObject scriptToJson = JObject.Parse(contentToScript.ToJsonString());

		//! СЕРВЕР МОЖЕТ ЗАБЛОКИРОВАТЬ ДОСТУП В ЭТОМ МЕСТЕ ИЗ-ЗА ПЕРЕГРУЗКИ
		// Основной элемент для парсинга называется 'd'
		JProperty dProperty = scriptToJson.Descendants()
			.OfType<JProperty>()
			.FirstOrDefault(p => p.Value.ToString() == "'d'")
				?? throw new AccessDeniedError("Сервер временно заблокировал доступ.");

		// Очень хрупкое следование структуре DuckDuckGo
		// На 2 вложенностях назад от ключевого элемента находится массив с токенами результатов
		JProperty arrayProperty = dProperty.Parent!.Parent!.Descendants()
			.OfType<JProperty>()
			.FirstOrDefault(p => p.Value.ToString() == "ArrayExpression")
				?? throw new DataParseError("Массив с элементами не найден.");

		// На еще 1 вложенности назад находятся сами токены результатов
		JToken tokens = scriptToJson.SelectToken(arrayProperty.Parent!.Path + ".elements") 
			?? throw new DataParseError("Токены результатов не найдены.");

		return tokens.Children();
	}

	// Перевод токенов в структуру Result
	public static List<Result> ParseResults(IEnumerable<JToken> tokens, out string nextPageUri) {
		var results = new List<Result>(tokens.Count());

		nextPageUri = "";

		foreach (var token in tokens) {
			// У каждого токена еще много своих свойств
			JToken tokenProperties = token.SelectToken("properties")
				?? throw new DataParseError($"Токен {token} не содержит свойств.");

			var result = new Result();

			foreach (var resultKeyValuePair in tokenProperties) {
				string resultKey = resultKeyValuePair["key"]?["value"]?.ToString()
					?? $"Данные не найдены...";

				string resultValue = resultKeyValuePair["value"]?["value"]?.ToString()
					?? $"Данные не найдены...";

				if (result.ParsedKeys.ContainsKey(resultKey)) {
					result.ParsedKeys[resultKey] = resultValue;
				}
				// Ключ "n" содержит ссылку для следующей страницы
				else if (resultKey == "n") { 
					nextPageUri = string.Format(NewPageBaseUrl, resultValue);
				}
			}

			// Чтобы отсеять технический мусор, добавляются только результаты с ссылками
			if (result.Url.Length > 0) 
				results.Add(result);
		}

		return results;
	}
}

internal static class HttpClientExtension {
	public static void AddUsingDictionary(this HttpHeaders destination, Dictionary<string, string> headers) {
		foreach (KeyValuePair<string, string> header in headers) {
			destination.Add(header.Key, header.Value);
		}
	}
}

public class DataParseError : Exception {
	public DataParseError (string message) : base(message) { }
}

public class AccessDeniedError : Exception {
	public AccessDeniedError (string message) : base(message) { }
}