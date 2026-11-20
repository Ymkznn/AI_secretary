using System.Net;
using System.Net.Http;
using System.Security;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AiSecretary.Desktop.Agent;
using AiSecretary.Desktop.Models;
using Microsoft.Extensions.Configuration;

namespace AiSecretary.Desktop.Services;

public interface IMailBroker
{
    bool IsConfigured { get; }
    Task<IReadOnlyList<MailItem>> FetchRecentMailsAsync(DateTimeOffset? since, CancellationToken ct = default, string? query = null, int? maxResults = null, string? folder = null);
    Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken ct = default);
    Task<MailItem?> GetMailByIdAsync(string mailId, CancellationToken ct = default);
}

/// <summary>
/// Direct in-process read-only Exchange EWS SOAP client using Windows Integrated Auth (UseDefaultCredentials).
/// </summary>
public sealed class EwsDirectMailBroker : IMailBroker
{
    private static readonly XNamespace TNs = "http://schemas.microsoft.com/exchange/services/2006/types";
    private readonly IHttpClientFactory _httpFactory;
    private readonly GuardService _guard;
    private readonly string _ewsUrl;
    private readonly int _maxEntries;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _folderIdsByName = new(StringComparer.OrdinalIgnoreCase);

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_ewsUrl);

    public EwsDirectMailBroker(IHttpClientFactory httpFactory, IConfiguration config, GuardService guard)
    {
        _httpFactory = httpFactory;
        _guard = guard;
        _ewsUrl = config.GetValue<string>("Ews:Url") ?? "";
        _maxEntries = config.GetValue<int>("Ews:MaxEntries", 20);
    }

    public async Task<IReadOnlyList<string>> ListFoldersAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_ewsUrl))
            throw new InvalidOperationException("EWS URL is not configured (set Ews:Url in appsettings.json).");

        var soap = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope
              xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
              xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
              xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
              <soap:Header>
                <t:RequestServerVersion Version="Exchange2013" />
              </soap:Header>
              <soap:Body>
                <m:FindFolder Traversal="Deep">
                  <m:FolderShape>
                    <t:BaseShape>Default</t:BaseShape>
                  </m:FolderShape>
                  <m:IndexedPageFolderView MaxEntriesReturned="100" Offset="0" BasePoint="Beginning" />
                  <m:ParentFolderIds>
                    <t:DistinguishedFolderId Id="msgfolderroot" />
                  </m:ParentFolderIds>
                </m:FindFolder>
              </soap:Body>
            </soap:Envelope>
            """;

        var xml = await SendSoapAsync("FindFolder", soap, ct);
        var names = new List<string>();
        foreach (var f in xml.Descendants(TNs + "Folder"))
        {
            var id = f.Element(TNs + "FolderId")?.Attribute("Id")?.Value;
            var name = f.Element(TNs + "DisplayName")?.Value?.Trim();
            if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            {
                _folderIdsByName[name] = id;
                var total = f.Element(TNs + "TotalCount")?.Value;
                var unread = f.Element(TNs + "UnreadCount")?.Value;
                names.Add(!string.IsNullOrWhiteSpace(unread) && unread != "0"
                    ? $"{name} (unread={unread}, total={total})"
                    : $"{name} (total={total ?? "?"})");
            }
        }
        return names;
    }

    private async Task<string> ResolveFolderIdXmlAsync(string? folder, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return """<t:DistinguishedFolderId Id="inbox" />""";

        var key = folder.Trim();
        var distinguished = key.ToLowerInvariant() switch
        {
            "inbox" or "收件匣" or "收件夾" => "inbox",
            "sent" or "sentitems" or "sent items" or "寄件備份" => "sentitems",
            "drafts" or "draft" or "草稿" => "drafts",
            "deleted" or "deleteditems" or "deleted items" or "trash" or "刪除的郵件" => "deleteditems",
            "junk" or "junkemail" or "spam" or "垃圾郵件" => "junkemail",
            "archive" or "封存" => "archive",
            _ => null
        };
        if (distinguished is not null)
            return $"""<t:DistinguishedFolderId Id="{distinguished}" />""";

        if (!_folderIdsByName.TryGetValue(key, out var folderId))
        {
            await ListFoldersAsync(ct);
            if (!_folderIdsByName.TryGetValue(key, out folderId))
            {
                var partial = _folderIdsByName.FirstOrDefault(kv => kv.Key.Contains(key, StringComparison.OrdinalIgnoreCase));
                folderId = partial.Value;
            }
        }

        if (string.IsNullOrWhiteSpace(folderId))
            throw new InvalidOperationException($"Mail folder '{key}' not found. Call manage_mail(action=\"list_folders\") to see available folders.");

        return $"""<t:FolderId Id="{SecurityElement.Escape(folderId)}" />""";
    }

    public async Task<IReadOnlyList<MailItem>> FetchRecentMailsAsync(DateTimeOffset? since, CancellationToken ct = default, string? query = null, int? maxResults = null, string? folder = null)
    {
        if (string.IsNullOrWhiteSpace(_ewsUrl))
            throw new InvalidOperationException("EWS URL is not configured (set Ews:Url in appsettings.json).");

        var targetCount = Math.Clamp(maxResults ?? _maxEntries, 1, 100);
        var cleanQuery = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        var folderXml = await ResolveFolderIdXmlAsync(folder, ct);
        var list = new List<MailItem>();

        if (cleanQuery is not null)
        {
            // 1) Try server-side Exchange Search (<m:QueryString>) across the target folder
            try
            {
                var xml = await SendSoapAsync("FindItem", BuildFindItemSoap(targetCount, 0, cleanQuery, folderXml), ct);
                await AppendMatchedMessagesAsync(xml, since, null, targetCount, list, ct);
                return list;
            }
            catch
            {
                // 2) Fallback: scan metadata in pages (up to 500 items), fetch full body only on matches
                const int pageSize = 100;
                const int maxScan = 500;
                for (int offset = 0; offset < maxScan && list.Count < targetCount; offset += pageSize)
                {
                    var xml = await SendSoapAsync("FindItem", BuildFindItemSoap(pageSize, offset, null, folderXml), ct);
                    int fetchedInPage = await AppendMatchedMessagesAsync(xml, since, cleanQuery, targetCount, list, ct);
                    if (fetchedInPage < pageSize) break;
                }
                return list;
            }
        }

        var recentXml = await SendSoapAsync("FindItem", BuildFindItemSoap(targetCount, 0, null, folderXml), ct);
        await AppendMatchedMessagesAsync(recentXml, since, null, targetCount, list, ct);
        return list;
    }

    private async Task<int> AppendMatchedMessagesAsync(
        XDocument xml,
        DateTimeOffset? since,
        string? metadataFilterQuery,
        int targetCount,
        List<MailItem> list,
        CancellationToken ct)
    {
        int rawCount = 0;
        foreach (var msg in xml.Descendants(TNs + "Message"))
        {
            rawCount++;
            if (list.Count >= targetCount) continue;

            var id = msg.Element(TNs + "ItemId")?.Attribute("Id")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(id)) continue;

            var receivedRaw = msg.Element(TNs + "DateTimeReceived")?.Value;
            var received = DateTimeOffset.TryParse(receivedRaw, out var dto) ? dto : DateTimeOffset.UtcNow;
            if (since.HasValue && received <= since.Value) continue;

            var subject = msg.Element(TNs + "Subject")?.Value ?? "(no subject)";
            var mailbox = msg.Element(TNs + "From")?.Element(TNs + "Mailbox");
            var senderName = mailbox?.Element(TNs + "Name")?.Value ?? "";
            var senderEmail = mailbox?.Element(TNs + "EmailAddress")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(senderName)) senderName = senderEmail;

            if (metadataFilterQuery is not null)
            {
                bool metaMatch = subject.Contains(metadataFilterQuery, StringComparison.OrdinalIgnoreCase)
                    || senderName.Contains(metadataFilterQuery, StringComparison.OrdinalIgnoreCase)
                    || senderEmail.Contains(metadataFilterQuery, StringComparison.OrdinalIgnoreCase);
                if (!metaMatch) continue;
            }

            var impRaw = msg.Element(TNs + "Importance")?.Value ?? "Normal";
            var importance = Enum.TryParse<MailImportance>(impRaw, true, out var imp) ? imp : MailImportance.Normal;
            var isRead = bool.TryParse(msg.Element(TNs + "IsRead")?.Value, out var r) && r;
            var threadId = msg.Element(TNs + "ConversationId")?.Attribute("Id")?.Value ?? id;

            var detail = await GetMailByIdAsync(id, ct);
            var body = detail?.Body ?? "";
            var snippet = body.Length > 80 ? body[..80].Replace("\n", " ") + "…" : body.Replace("\n", " ");

            list.Add(new MailItem(id, subject, senderName, senderEmail, received, importance, snippet, body, isRead, threadId));
        }
        return rawCount;
    }

    private static string BuildFindItemSoap(int maxEntries, int offset, string? queryString, string folderIdXml)
    {
        var queryXml = string.IsNullOrWhiteSpace(queryString)
            ? ""
            : $"\n                  <m:QueryString>{SecurityElement.Escape(queryString)}</m:QueryString>";

        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope
              xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
              xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
              xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
              <soap:Header>
                <t:RequestServerVersion Version="Exchange2013" />
              </soap:Header>
              <soap:Body>
                <m:FindItem Traversal="Shallow">
                  <m:ItemShape>
                    <t:BaseShape>Default</t:BaseShape>
                    <t:AdditionalProperties>
                      <t:FieldURI FieldURI="item:Subject" />
                      <t:FieldURI FieldURI="item:DateTimeReceived" />
                      <t:FieldURI FieldURI="item:Importance" />
                      <t:FieldURI FieldURI="message:From" />
                      <t:FieldURI FieldURI="message:IsRead" />
                      <t:FieldURI FieldURI="item:ConversationId" />
                    </t:AdditionalProperties>
                  </m:ItemShape>
                  <m:IndexedPageItemView MaxEntriesReturned="{maxEntries}" Offset="{offset}" BasePoint="Beginning" />
                  <m:SortOrder>
                    <t:FieldOrder Order="Descending">
                      <t:FieldURI FieldURI="item:DateTimeReceived" />
                    </t:FieldOrder>
                  </m:SortOrder>
                  <m:ParentFolderIds>
                    {folderIdXml}
                  </m:ParentFolderIds>{queryXml}
                </m:FindItem>
              </soap:Body>
            </soap:Envelope>
            """;
    }

    public async Task<MailItem?> GetMailByIdAsync(string itemId, CancellationToken ct = default)
    {
        var escapedId = SecurityElement.Escape(itemId);
        var soap = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope
              xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
              xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
              xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
              <soap:Header>
                <t:RequestServerVersion Version="Exchange2013" />
              </soap:Header>
              <soap:Body>
                <m:GetItem>
                  <m:ItemShape>
                    <t:BaseShape>Default</t:BaseShape>
                    <t:BodyType>Text</t:BodyType>
                    <t:AdditionalProperties>
                      <t:FieldURI FieldURI="item:Body" />
                      <t:FieldURI FieldURI="item:Subject" />
                      <t:FieldURI FieldURI="item:DateTimeReceived" />
                      <t:FieldURI FieldURI="item:Importance" />
                      <t:FieldURI FieldURI="message:From" />
                      <t:FieldURI FieldURI="message:IsRead" />
                    </t:AdditionalProperties>
                  </m:ItemShape>
                  <m:ItemIds>
                    <t:ItemId Id="{escapedId}" />
                  </m:ItemIds>
                </m:GetItem>
              </soap:Body>
            </soap:Envelope>
            """;

        var xml = await SendSoapAsync("GetItem", soap, ct);
        var msg = xml.Descendants(TNs + "Message").FirstOrDefault();
        if (msg is null) return null;

        var subject = msg.Element(TNs + "Subject")?.Value ?? "(no subject)";
        var mailbox = msg.Element(TNs + "From")?.Element(TNs + "Mailbox");
        var senderName = mailbox?.Element(TNs + "Name")?.Value ?? "";
        var senderEmail = mailbox?.Element(TNs + "EmailAddress")?.Value ?? "";
        if (string.IsNullOrWhiteSpace(senderName)) senderName = senderEmail;

        var receivedRaw = msg.Element(TNs + "DateTimeReceived")?.Value;
        var received = DateTimeOffset.TryParse(receivedRaw, out var dto) ? dto : DateTimeOffset.UtcNow;
        var impRaw = msg.Element(TNs + "Importance")?.Value ?? "Normal";
        var importance = Enum.TryParse<MailImportance>(impRaw, true, out var imp) ? imp : MailImportance.Normal;
        var isRead = bool.TryParse(msg.Element(TNs + "IsRead")?.Value, out var r) && r;
        var body = CleanBody(msg.Element(TNs + "Body")?.Value ?? "");
        var snippet = body.Length > 80 ? body[..80].Replace("\n", " ") + "…" : body.Replace("\n", " ");

        return new MailItem(itemId, subject, senderName, senderEmail, received, importance, snippet, body, isRead, itemId);
    }

    private async Task<XDocument> SendSoapAsync(string soapAction, string soapBody, CancellationToken ct)
    {
        var permId = soapAction == "FindItem" ? "ews.find_item" : soapAction == "GetItem" ? "ews.get_item" : $"ews.{soapAction}";
        var guardCheck = _guard.CheckRestApiAccess(permId, "POST", _ewsUrl);
        if (!guardCheck.Allowed) throw new UnauthorizedAccessException(guardCheck.Reason);

        var client = _httpFactory.CreateClient("EwsClient");
        using var req = new HttpRequestMessage(HttpMethod.Post, _ewsUrl);
        req.Headers.Add("SOAPAction", soapAction);
        req.Content = new StringContent(soapBody, Encoding.UTF8, "text/xml");

        using var resp = await client.SendAsync(req, ct);
        var xmlText = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"EWS {soapAction} failed ({(int)resp.StatusCode}): {xmlText}");
        }

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            MaxCharactersInDocument = 10_000_000
        };
        using var sr = new StringReader(xmlText);
        using var reader = XmlReader.Create(sr, settings);
        return XDocument.Load(reader);
    }

    private static string CleanBody(string raw)
    {
        var noTags = Regex.Replace(raw, "<[^>]+>", " ");
        return WebUtility.HtmlDecode(noTags).Trim();
    }
}
