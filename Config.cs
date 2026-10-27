using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace DuckDuckGo.SearchBot.Configuration;

public static class Config {
	private static readonly string _path = AppContext.BaseDirectory + "appsettings.json";
	private static readonly JObject _json = GetCurrentFile();
	
	public static JObject AppSettings { get => _json; }

	private static JObject GetCurrentFile() {
		string jString = File.ReadAllText(_path);

		return JObject.Parse(jString);
	}
}