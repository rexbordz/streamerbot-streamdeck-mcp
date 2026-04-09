using System;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Linq;
using System.Collections.Generic;


public class CPHInline

{
    private readonly string rootDir = AppDomain.CurrentDomain.BaseDirectory;
	private Process _elgatoMcpProcess;
	private string _mcpSessionId;
	private const string McpUrl = "http://127.0.0.1:9090/mcp";
	private Dictionary<string, string> _actions = new Dictionary<string, string>();
    
    public bool Execute()
	{
		LaunchElgatoMcpServer();

		if (!InitializeMcpSession())
		{
			CPH.LogWarn("[ElgatoMCP][Init] MCP session initialization failed.");
		}

		if (!RefreshExecutableActions())
		{
			CPH.LogWarn("[ElgatoMCP][Init] Failed to load actions.");
		}

		CPH.SetGlobalVar("[SD MCP] SessionId", _mcpSessionId, false);
		CPH.LogInfo("[ElgatoMCP][Init] MCP ready.");
		
		return true;
	}

	public void Dispose()
	{
		StopElgatoMcpServer();
	}

	/********************
	* EXECUTABLE METHODS
	*********************/
	private bool InitializeMcpSession()
	{
		using (var client = new HttpClient())
		{
			client.Timeout = TimeSpan.FromSeconds(5);

			for (int attempt = 0; attempt < 15; attempt++)
			{
				try
				{
					client.DefaultRequestHeaders.Clear();
					client.DefaultRequestHeaders.Add("Accept", "application/json, text/event-stream");

					string initializeJson =
						"{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"clientInfo\":{\"name\":\"Streamer.bot\",\"version\":\"1.0.0\"}}}";

					var initContent = new StringContent(initializeJson, Encoding.UTF8, "application/json");
					var initResponse = client.PostAsync(McpUrl, initContent).Result;

					if (!initResponse.IsSuccessStatusCode)
					{
						Thread.Sleep(1000);
						continue;
					}

					if (!initResponse.Headers.Contains("mcp-session-id"))
					{
						CPH.LogWarn("[ElgatoMCP][Init] Initialize response missing mcp-session-id header.");
						Thread.Sleep(1000);
						continue;
					}

					_mcpSessionId = initResponse.Headers.GetValues("mcp-session-id").FirstOrDefault();

					if (string.IsNullOrWhiteSpace(_mcpSessionId))
					{
						CPH.LogWarn("[ElgatoMCP][Init] MCP session ID was empty.");
						Thread.Sleep(1000);
						continue;
					}

					client.DefaultRequestHeaders.Clear();
					client.DefaultRequestHeaders.Add("Accept", "application/json, text/event-stream");
					client.DefaultRequestHeaders.Add("Mcp-Session-Id", _mcpSessionId);

					string initializedJson =
						"{\"jsonrpc\":\"2.0\",\"method\":\"notifications/initialized\"}";

					var initializedContent = new StringContent(initializedJson, Encoding.UTF8, "application/json");
					var initializedResponse = client.PostAsync(McpUrl, initializedContent).Result;

					if ((int)initializedResponse.StatusCode == 202 || initializedResponse.IsSuccessStatusCode)
					{
						CPH.LogInfo($"[ElgatoMCP][Init] MCP session initialized. SessionId={_mcpSessionId}");
						return true;
					}

					CPH.LogWarn($"[ElgatoMCP][Init] notifications/initialized failed with status {(int)initializedResponse.StatusCode}.");
				}
				catch (Exception ex)
				{
					CPH.LogWarn($"[ElgatoMCP][Init] Attempt {attempt + 1} failed: {ex.Message}");
				}

				Thread.Sleep(1000);
			}
		}

		return false;
	}

	public bool RefreshExecutableActions()
	{
		try
		{
			using (var client = new HttpClient())
			{
				client.Timeout = TimeSpan.FromSeconds(5);

				client.DefaultRequestHeaders.Clear();
				client.DefaultRequestHeaders.Add("Accept", "application/json, text/event-stream");
				client.DefaultRequestHeaders.Add("Mcp-Session-Id", _mcpSessionId);

				string requestJson =
					"{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"tools/call\",\"params\":{\"name\":\"streamdeck__get_executable_actions\",\"arguments\":{}}}";

				var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
				var response = client.PostAsync(McpUrl, content).Result;

				if (!response.IsSuccessStatusCode)
				{
					CPH.LogWarn($"[ElgatoMCP][Actions] Request failed with status {(int)response.StatusCode}");
					return false;
				}

				string raw = response.Content.ReadAsStringAsync().Result;

				// Extract JSON inside "data: ..."
				int dataIndex = raw.IndexOf("data:");
				if (dataIndex == -1)
				{
					CPH.LogWarn("[ElgatoMCP][Actions] Could not find data payload.");
					return false;
				}

				string jsonPart = raw.Substring(dataIndex + 5).Trim();

				var outer = JObject.Parse(jsonPart);

				var textContent = outer["result"]?["content"]?[0]?["text"]?.ToString();

				if (string.IsNullOrWhiteSpace(textContent))
				{
					CPH.LogWarn("[ElgatoMCP][Actions] Empty content.");
					return false;
				}

				var inner = JObject.Parse(textContent);
				var actions = inner["actions"] as JArray;

				if (actions == null)
				{
					CPH.LogWarn("[ElgatoMCP][Actions] No actions found.");
					return false;
				}

				_actions.Clear();

				foreach (var action in actions)
				{
					string title = action["title"]?.ToString();
					string id = action["id"]?.ToString();
					string actionName = action["description"]?["name"]?.ToString();
					string pluginId = action["pluginId"]?.ToString();

					if (string.IsNullOrWhiteSpace(id))
						continue;

					string globalKey;

					if (!string.IsNullOrWhiteSpace(title))
					{
						globalKey = "[SD] " + title;
					}
					else
					{
						if (string.IsNullOrWhiteSpace(pluginId))
							pluginId = "UnknownPlugin";

						if (string.IsNullOrWhiteSpace(actionName))
							actionName = "UnknownAction";

						int index = 1;
						globalKey = $"[SD] {pluginId} {actionName}_{index}";

						while (CPH.GetGlobalVar<string>(globalKey, false) != null)
						{
							index++;
							globalKey = $"[SD] {pluginId} {actionName}_{index}";
						}
					}

					_actions[title ?? globalKey] = id;
					CPH.SetGlobalVar(globalKey, id, false);

					CPH.LogInfo($"[ElgatoMCP][Actions] {globalKey} → {id}");
				}

				CPH.LogInfo($"[ElgatoMCP][Actions] Loaded {_actions.Count} actions.");

				return true;
			}
		}
		catch (Exception ex)
		{
			CPH.LogWarn($"[ElgatoMCP][Actions] Failed: {ex.Message}");
			return false;
		}
	}

	public bool ExecuteMcpAction()
	{
		CPH.TryGetArg("actionInput", out string actionInput);

		try
		{
			if (string.IsNullOrWhiteSpace(actionInput))
			{
				CPH.LogWarn("[ElgatoMCP][Execute] Action input was empty.");
				return false;
			}

			if (string.IsNullOrWhiteSpace(_mcpSessionId))
			{
				_mcpSessionId = CPH.GetGlobalVar<string>("[SD MCP] SessionId", false);
			}

			if (string.IsNullOrWhiteSpace(_mcpSessionId))
			{
				CPH.LogWarn("[ElgatoMCP][Execute] MCP session ID was not available.");
				return false;
			}

			string resolvedId = ResolveActionId(actionInput);

			if (string.IsNullOrWhiteSpace(resolvedId))
			{
				CPH.LogWarn($"[ElgatoMCP][Execute] Could not resolve action: '{actionInput}'");
				return false;
			}

			using (var client = new HttpClient())
			{
				client.Timeout = TimeSpan.FromSeconds(5);

				client.DefaultRequestHeaders.Clear();
				client.DefaultRequestHeaders.Add("Accept", "application/json, text/event-stream");
				client.DefaultRequestHeaders.Add("Mcp-Session-Id", _mcpSessionId);

				string escapedId = EscapeJson(resolvedId);

				string requestJson =
					"{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"tools/call\",\"params\":{\"name\":\"streamdeck__execute_action\",\"arguments\":{\"id\":\"" + escapedId + "\"}}}";

				var content = new StringContent(requestJson, Encoding.UTF8, "application/json");
				var response = client.PostAsync(McpUrl, content).Result;

				if (!response.IsSuccessStatusCode)
				{
					CPH.LogWarn($"[ElgatoMCP][Execute] Request failed with status {(int)response.StatusCode}");
					return false;
				}

				string raw = response.Content.ReadAsStringAsync().Result;

				int dataIndex = raw.IndexOf("data:");
				if (dataIndex == -1)
				{
					CPH.LogWarn("[ElgatoMCP][Execute] Could not find data payload.");
					return false;
				}

				string jsonPart = raw.Substring(dataIndex + 5).Trim();
				var outer = JObject.Parse(jsonPart);

				string textContent = outer["result"]?["content"]?[0]?["text"]?.ToString();

				if (string.IsNullOrWhiteSpace(textContent))
				{
					CPH.LogWarn("[ElgatoMCP][Execute] Empty result content.");
					return false;
				}

				var inner = JObject.Parse(textContent);
				string status = inner["status"]?.ToString();

				if (string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
				{
					CPH.LogInfo($"[ElgatoMCP][Execute] Executed action '{actionInput}' -> '{resolvedId}'.");
					return true;
				}

				CPH.LogWarn($"[ElgatoMCP][Execute] Unexpected status for '{actionInput}': {textContent}");
				return false;
			}
		}
		catch (Exception ex)
		{
			CPH.LogWarn($"[ElgatoMCP][Execute] Failed for '{actionInput}': {ex.Message}");
			return false;
		}
	}

	/*****************
	* PROCESS METHODS
	******************/

	private void LaunchElgatoMcpServer()
	{
		try
		{
			string batPath = Path.Combine(rootDir, "ElgatoMcpBridge", "start-mcp.bat");

			if (!File.Exists(batPath))
			{
				CPH.LogWarn($"[ElgatoMCP][Init] Launch skipped: file not found at '{batPath}'");
				return;
			}

			var startInfo = new ProcessStartInfo
			{
				FileName = "cmd.exe",
				Arguments = $"/c \"{batPath}\"",
				WorkingDirectory = Path.GetDirectoryName(batPath),
				UseShellExecute = false,
				CreateNoWindow = true
			};

			_elgatoMcpProcess = Process.Start(startInfo);

			CPH.LogInfo($"[ElgatoMCP][Init] Launch attempted: '{batPath}'");
		}
		catch (Exception ex)
		{
			CPH.LogWarn($"[ElgatoMCP][Init] Launch failed: {ex.Message}");
		}
	}

	private void StopElgatoMcpServer()
	{
		try
		{
			if (_elgatoMcpProcess == null || _elgatoMcpProcess.HasExited)
				return;

			int pid = _elgatoMcpProcess.Id;

			// Kill entire process tree using taskkill
			var psi = new ProcessStartInfo
			{
				FileName = "taskkill",
				Arguments = $"/PID {pid} /T /F",
				CreateNoWindow = true,
				UseShellExecute = false
			};

			Process.Start(psi);

			CPH.LogInfo($"[ElgatoMCP][Dispose] Killed process tree (PID={pid}).");
		}
		catch (Exception ex)
		{
			CPH.LogWarn($"[ElgatoMCP][Dispose] Failed to stop MCP server: {ex.Message}");
		}
	}

	/****************
	* HELPER METHODS
	*****************/

	private string SanitizeKey(string input)
	{
		var invalid = Path.GetInvalidFileNameChars();
		foreach (var c in invalid)
			input = input.Replace(c.ToString(), "");

		return input.Replace(" ", "_");
	}

	public string ResolveActionId(string actionInput)
	{
		if (string.IsNullOrWhiteSpace(actionInput))
			return null;

		string trimmed = actionInput.Trim();

		// 0. Assume direct ID first
		if (trimmed.Contains("-"))
			return trimmed;

		// 1. Exact title match from cache
		if (_actions != null && _actions.ContainsKey(trimmed))
			return _actions[trimmed];

		// 2. Case-insensitive title match from cache
		if (_actions != null)
		{
			foreach (var kv in _actions)
			{
				if (string.Equals(kv.Key, trimmed, StringComparison.OrdinalIgnoreCase))
					return kv.Value;
			}
		}

		// 3. Global variable lookup
		string globalKey = "[SD]" + SanitizeKey(trimmed);
		string idFromGlobal = CPH.GetGlobalVar<string>(globalKey, false);

		if (!string.IsNullOrWhiteSpace(idFromGlobal))
			return idFromGlobal;

		return null;
	}

	public string EscapeJson(string value)
	{
		return value
			.Replace("\\", "\\\\")
			.Replace("\"", "\\\"");
	}


}