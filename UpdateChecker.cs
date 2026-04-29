using rexbordzUI;
using System;
using System.Threading.Tasks;

public class CPHInline
{
	public void Init()
	{
        string widgetTitle = "Stream Deck MCP";
		try
        {
            var checker = new UpdateChecker();

            Task.Run(async () =>
            {
                await checker.CheckForUpdate(
                    widgetTitle,               
                    "1.0.0",                         
                    "https://raw.githubusercontent.com/rexbordz/streamerbot-streamdeck-mcp/refs/heads/main/version.json"  // JSON URL
                );
            });
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[{widgetTitle}][UpdateChecker] Update Checker failed: {ex.Message}");
        }
	}
}