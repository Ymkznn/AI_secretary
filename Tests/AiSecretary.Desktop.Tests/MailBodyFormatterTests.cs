using AiSecretary.Desktop.Services;
using Xunit;

namespace AiSecretary.Desktop.Tests;

public sealed class MailBodyFormatterTests
{
    [Fact]
    public void HtmlToReadableText_StripsCssScriptComments_AndPreservesParagraphsListsAndTableColumns()
    {
        const string html = """
            <!DOCTYPE html>
            <html>
            <head>
              <title>Hidden Title</title>
              <style>.btn { color: red; font-size: 14px; }</style>
              <script>alert('ignore');</script>
            </head>
            <body>
              <!-- tracking comment -->
              <p>親愛的使用者您好，</p>
              <p>您的訂單已出貨：</p>
              <table>
                <tr><td>訂單編號</td><td>#ORD-2026-9981</td></tr>
                <tr><td>配送方式</td><td>EMS 國際快捷</td></tr>
              </table>
              <ul>
                <li>步驟一：確認收件地址</li>
                <li>步驟二：點擊 <a href="https://track.example.com/ems?id=12345">追蹤包裹</a></li>
              </ul>
              <hr/>
              <div>感謝您的使用&nbsp;&nbsp;&nbsp;&nbsp;</div>
            </body>
            </html>
            """;

        var readable = MailBodyFormatter.HtmlToReadableText(html);

        Assert.DoesNotContain(".btn", readable);
        Assert.DoesNotContain("alert(", readable);
        Assert.DoesNotContain("tracking comment", readable);
        Assert.DoesNotContain("Hidden Title", readable);
        Assert.Contains("親愛的使用者您好，", readable);
        Assert.Contains("訂單編號  #ORD-2026-9981", readable);
        Assert.Contains("• 步驟一：確認收件地址", readable);
        Assert.Contains("追蹤包裹 (https://track.example.com/ems?id=12345)", readable);
        Assert.Contains("────────────────────────", readable);
        Assert.Contains("\n\n", readable); // Paragraph separation preserved
    }

    [Fact]
    public void NormalizeReadablePlainText_RemovesZeroWidthJunk_AndCollapsesExcessiveBlankLines()
    {
        var messy = "主旨通知\u034F\u200B\u200C\uFEFF\r\n\r\n\r\n\r\n   \u00A0\u034F   \r\n第一段內容\r\n\r\n\r\n第二段內容   ";
        var cleaned = MailBodyFormatter.NormalizeReadablePlainText(messy);

        Assert.Equal("主旨通知\n\n第一段內容\n\n第二段內容", cleaned);
    }

    [Fact]
    public void FormatDisplayUrl_ShortensLongTrackingUrlsCleanly()
    {
        const string longUrl = "https://click.notifications.example.com/ls/click?upn=u001.veryLongTrackingToken1234567890abcdefghijklmnopqrstuvwxyz";
        var display = MailBodyFormatter.FormatDisplayUrl(longUrl, 58);

        Assert.True(display.Length <= 60);
        Assert.StartsWith("click.notifications.example.com/ls/click", display);
        Assert.EndsWith("…", display);

        const string shortUrl = "https://github.com/Ymkznn/AI_secretary";
        Assert.Equal(shortUrl, MailBodyFormatter.FormatDisplayUrl(shortUrl, 58));
    }
}
