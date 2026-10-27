using System.Collections.Concurrent;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Exceptions;

using DuckDuckGo.SearchBot.Configuration;
using DuckDuckGo.SearchBot.Engine;

namespace DuckDuckGo.SearchBot;

internal class Program {
	private static TelegramBotClient s_bot = null!;
	private static SearchApi s_searchApi = new SearchApi();
	
	// Временное хранилище запросов на поиск
	private static ConcurrentDictionary<string, (IAsyncEnumerator<List<Result>> enumerator, DateTime date)> s_requestsToEngine = new ();

	// Временное хранилище запросов к боту
	private static ConcurrentDictionary<long, CancellationTokenSource> s_queriesToInline = new ();

 	static async Task Main(string[] args) {
		string token = Config.AppSettings.SelectToken("botSettings.token")?.ToString()!;

		using var cts = new CancellationTokenSource();

		s_bot = new TelegramBotClient(token, cancellationToken: cts.Token);

		s_bot.OnUpdate += OnUpdate;
		s_bot.OnError += OnError;

		// Чтобы бот не перегрузился множеством запоздалых запросов
		await s_bot.GetUpdates(offset: -1);

		// Спустя 5 минут запрос автоматически удаляется
		_ = Task.Run (async () => {
			while (true) {
				foreach (var request in s_requestsToEngine) {
					if ((DateTime.Now - request.Value.date).TotalMinutes >= 5) {
						await DeleteRequest(request.Key);
					}
				}

				await Task.Delay(5000);
			}
		});

		Console.ReadLine();
		cts.Cancel();
	}

	private static async Task DeleteRequest(string id) {
		await s_requestsToEngine[id].enumerator.DisposeAsync();
		s_requestsToEngine.TryRemove(id, out _);
	}

	private static async Task OnInlineQuery(InlineQuery query) {
		string id = $"{query.From.Id}:{query.Query}";

		// Чтобы от одного пользователя за раз не было нескольких запросов в памяти
		foreach (var key in s_requestsToEngine.Keys) {
			var parts = key.Split(":", 2);
			
			if (parts[0] == query.From.Id.ToString() && parts[1] != query.Query) {
				await DeleteRequest(id);
			}
		}

		// Или следующая порция результатов, или создание нового запроса
		if (!s_requestsToEngine.TryGetValue(id, out var request)) {
			request.enumerator = s_searchApi.Search(query.Query).GetAsyncEnumerator();
			request.date = DateTime.Now;

			s_requestsToEngine[id] = request;
		}

		var currentOffset = string.IsNullOrEmpty(query.Offset) ? 0 : int.Parse(query.Offset);
		var currentRequest = s_requestsToEngine[id];

		List<InlineQueryResult> answer = new List<InlineQueryResult>();
		List<Result> results;

		bool isMoveNext = await currentRequest.enumerator.MoveNextAsync();
		
		// Когда ссылки на следующую страницу не будет, запрос удаляется
		if (isMoveNext) {
			s_requestsToEngine[id] = (s_requestsToEngine[id].enumerator, DateTime.Now);
		}
		else {
			await DeleteRequest(id);
		}

		results = currentRequest.enumerator.Current;

		int answerItemId = currentOffset;
		foreach (var result in results) {
			answer.Add(
				new InlineQueryResultArticle() { 
					Id = answerItemId.ToString(),
					Title = result.Title, 
					Description = result.Description,
					
					ThumbnailUrl = result.Icon,

					InputMessageContent = new InputTextMessageContent(
						$"<b>{result.Title}</b>\n\n{result.Description}\n\n<b>Ссылка:</b> {result.Url}"
					) { ParseMode = ParseMode.Html },

					ReplyMarkup = new InlineKeyboardButton("Открыть в браузере", InlineButtonType.Url, result.Url)
				}
			);

			answerItemId++;
		}

		var nextOffset = currentOffset + results.Count;

		//? Иногда телеграм из-за собственных лимитов не хочет отображать дошедшие результаты
		await s_bot.AnswerInlineQuery(query.Id, answer, nextOffset: nextOffset.ToString());
	}

	private static async Task OnInlineResult(ChosenInlineResult query) {
		string id = $"{query.From.Id}:{query.Query}";

		if (s_requestsToEngine.ContainsKey(id)) {
			await DeleteRequest(id);
		}
	}

	private static async Task OnUpdate(Update update) {
		if ((update.InlineQuery is { } query) && !string.IsNullOrEmpty(query.Query)) {
			long id = query.From.Id;

			if (s_queriesToInline.TryRemove(id, out var oldCts)) {
				oldCts.Cancel();
				oldCts.Dispose();
			}

			var cts = new CancellationTokenSource();
			s_queriesToInline[id] = cts;

			_ = Task.Run( async() => {
				try {
					await Task.Delay(5000, cts.Token);

					await OnInlineQuery(query);
				}
				catch (OperationCanceledException) {
					
				}
				finally {
					if (s_queriesToInline.TryGetValue(id, out var currentCts) && currentCts == cts) {
						s_queriesToInline.TryRemove(id, out _);
						cts.Dispose();
					};
				}
			});
		}

		if (update.ChosenInlineResult is { } result) {
			await OnInlineResult(result);
		}
	}

	private static async Task OnError(Exception exception, HandleErrorSource source) {
		if (exception is RequestException) {
			return;
		}
		else {
			Console.WriteLine(exception);
		}
	}
}
