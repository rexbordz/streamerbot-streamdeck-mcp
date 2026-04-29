using System;
using System.IO;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;


public class CPHInline

{
    private readonly string rootDir = AppDomain.CurrentDomain.BaseDirectory;
	private Process _elgatoMcpProcess;
	private string _mcpSessionId;
	private const string McpUrl = "http://127.0.0.1:9090/mcp";
	private Dictionary<string, string> _actions = new Dictionary<string, string>();
	private const string SdKeyRegistryGlobal = "[SD MCP] ActionKeys";

	private IntPtr _mcpJobHandle = IntPtr.Zero;

	private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
	private const int JobObjectExtendedLimitInformation = 9;
    
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

		if (_mcpJobHandle != IntPtr.Zero)
		{
			CloseHandle(_mcpJobHandle);
			_mcpJobHandle = IntPtr.Zero;
		}
	}

	/********************
	* EXECUTABLE METHODS
	*********************/

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

				var currentGlobalKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
				var unnamedActionCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

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

						string unnamedBaseKey = $"{pluginId} {actionName}";

						int index;
						if (!unnamedActionCounts.TryGetValue(unnamedBaseKey, out index))
							index = 0;

						index++;
						unnamedActionCounts[unnamedBaseKey] = index;

						globalKey = $"[SD] {unnamedBaseKey}_{index}";
					}

					string actionLookupName = !string.IsNullOrWhiteSpace(title)
						? title
						: globalKey.Substring(5);

					_actions[actionLookupName] = id;
					CPH.SetGlobalVar(globalKey, id, false);
					currentGlobalKeys.Add(globalKey);

					CPH.LogInfo($"[ElgatoMCP][Actions] {globalKey} → {id}");
				}

				RemoveDeletedStreamDeckGlobals(currentGlobalKeys);
				SaveStreamDeckGlobalRegistry(currentGlobalKeys);

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
					var baseEx = ex.GetBaseException();
					CPH.LogWarn($"[ElgatoMCP][Init] Attempt {attempt + 1} failed: {baseEx.GetType().Name}: {baseEx.Message}");
				}

				Thread.Sleep(1000);
			}
		}

		return false;
	}

	// --- MCP SERVER PROCESS KILLER ---
	// This section is responsible for automatically killing the server when it detects that the parent app isn't running anymore
	// This avoids stale connection to the old Streamer.bot instance in case Streamer.bot crashed or was forced closed
	[DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
	private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string lpName);

	[DllImport("kernel32.dll")]
	private static extern bool SetInformationJobObject(
		IntPtr hJob,
		int jobObjectInfoClass,
		IntPtr lpJobObjectInfo,
		uint cbJobObjectInfoLength
	);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

	[DllImport("kernel32.dll", SetLastError = true)]
	private static extern bool CloseHandle(IntPtr hObject);

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
	{
		public long PerProcessUserTimeLimit;
		public long PerJobUserTimeLimit;
		public uint LimitFlags;
		public UIntPtr MinimumWorkingSetSize;
		public UIntPtr MaximumWorkingSetSize;
		public uint ActiveProcessLimit;
		public IntPtr Affinity;
		public uint PriorityClass;
		public uint SchedulingClass;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct IO_COUNTERS
	{
		public ulong ReadOperationCount;
		public ulong WriteOperationCount;
		public ulong OtherOperationCount;
		public ulong ReadTransferCount;
		public ulong WriteTransferCount;
		public ulong OtherTransferCount;
	}

	[StructLayout(LayoutKind.Sequential)]
	private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
	{
		public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
		public IO_COUNTERS IoInfo;
		public UIntPtr ProcessMemoryLimit;
		public UIntPtr JobMemoryLimit;
		public UIntPtr PeakProcessMemoryUsed;
		public UIntPtr PeakJobMemoryUsed;
	}

	private bool AssignProcessToKillOnCloseJob(Process process)
	{
		try
		{
			if (process == null || process.HasExited)
				return false;

			if (_mcpJobHandle == IntPtr.Zero)
			{
				_mcpJobHandle = CreateJobObject(IntPtr.Zero, null);

				if (_mcpJobHandle == IntPtr.Zero)
				{
					CPH.LogWarn("[ElgatoMCP][Init] Failed to create MCP job object.");
					return false;
				}

				var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
				info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;

				int length = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
				IntPtr infoPtr = Marshal.AllocHGlobal(length);

				try
				{
					Marshal.StructureToPtr(info, infoPtr, false);

					if (!SetInformationJobObject(_mcpJobHandle, JobObjectExtendedLimitInformation, infoPtr, (uint)length))
					{
						int error = Marshal.GetLastWin32Error();
						CPH.LogWarn($"[ElgatoMCP][Init] Failed to configure MCP job object. Win32Error={error}");
						return false;
					}
				}
				finally
				{
					Marshal.FreeHGlobal(infoPtr);
				}
			}

			if (!AssignProcessToJobObject(_mcpJobHandle, process.Handle))
			{
				int error = Marshal.GetLastWin32Error();
				CPH.LogWarn($"[ElgatoMCP][Init] Failed to assign MCP process to job object. Win32Error={error}");
				return false;
			}

			CPH.LogInfo("[ElgatoMCP][Init] MCP process assigned to kill-on-close job object.");
			return true;
		}
		catch (Exception ex)
		{
			CPH.LogWarn($"[ElgatoMCP][Init] Job object setup failed: {ex.GetType().Name}: {ex.Message}");
			return false;
		}
	}
	// --- END SECTION ---

	private void LaunchElgatoMcpServer()
	{
		try
		{
			var startInfo = new ProcessStartInfo
			{
				FileName = "cmd.exe",
				Arguments = "/c npx -y @elgato/mcp-server@latest --http",
				UseShellExecute = false,
				CreateNoWindow = true
			};

			_elgatoMcpProcess = Process.Start(startInfo);

			AssignProcessToKillOnCloseJob(_elgatoMcpProcess);

			CPH.LogInfo($"[ElgatoMCP][Init] Launch attempted (PID={_elgatoMcpProcess?.Id}).");
		}
		catch (Exception ex)
		{
			CPH.LogWarn($"[ElgatoMCP][Init] Launch failed: {ex.GetType().Name}: {ex.Message}");
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

	private void RemoveDeletedStreamDeckGlobals(HashSet<string> currentGlobalKeys)
	{
		try
		{
			var previousKeys = LoadStreamDeckGlobalRegistry();

			foreach (string oldKey in previousKeys)
			{
				if (currentGlobalKeys.Contains(oldKey))
					continue;

				CPH.UnsetGlobalVar(oldKey, false);
				CPH.LogInfo($"[ElgatoMCP][Refresh] Removed stale Stream Deck global: {oldKey}");
			}
		}
		catch (Exception ex)
		{
			CPH.LogWarn($"[ElgatoMCP][Refresh] Failed to remove stale globals: {ex.GetType().Name}: {ex.Message}");
		}
	}

	private HashSet<string> LoadStreamDeckGlobalRegistry()
	{
		var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

		string json = CPH.GetGlobalVar<string>(SdKeyRegistryGlobal, false);

		if (string.IsNullOrWhiteSpace(json))
			return keys;

		try
		{
			var array = JArray.Parse(json);

			foreach (var item in array)
			{
				string key = item?.ToString();

				if (!string.IsNullOrWhiteSpace(key))
					keys.Add(key);
			}
		}
		catch
		{
			// If the registry is corrupted, ignore it and rebuild on this refresh.
		}

		return keys;
	}

	private void SaveStreamDeckGlobalRegistry(HashSet<string> currentGlobalKeys)
	{
		var array = new JArray();

		foreach (string key in currentGlobalKeys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
			array.Add(key);

		CPH.SetGlobalVar(SdKeyRegistryGlobal, array.ToString(Newtonsoft.Json.Formatting.None), false);
	}

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