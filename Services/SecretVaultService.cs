using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AiSecretary.Desktop.Services;

public sealed record SecretMetadata(
    string Handle,
    string Kind,
    string? SenderDomain,
    string? SenderName,
    string? MailId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt);

/// <summary>
/// Pure in-memory, single-use secret vault for short-lived OTP codes (TTL 5-10 min; default 8 min).
/// - NEVER writes secrets to SQLite, files, logs, observations, or Blackboard.
/// - Issues opaque handles of the form <c>otp#&lt;6 hex chars&gt;</c>.
/// - Only <see cref="Skills.FillOtpSkill"/>, zero-LLM L2 auto-fill, and the clipboard "copy OTP" button
///   may consume a handle to retrieve the plaintext value (and consumption immediately invalidates the handle).
/// </summary>
public sealed class SecretVaultService
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(8);
    private static readonly Regex HandlePattern = new(@"^otp#[0-9a-f]{6}$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private sealed record VaultEntry(string Value, SecretMetadata Metadata);
    private sealed record ConsumedRedactionEntry(string Value, string Handle, string? MailId, DateTimeOffset ExpiresAt);

    private readonly object _lock = new();
    private readonly Dictionary<string, VaultEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _mailIdToHandle = new(StringComparer.Ordinal);
    private readonly List<ConsumedRedactionEntry> _recentlyConsumed = new();
    private readonly Func<DateTimeOffset> _utcNow;
    private string? _lastActiveHandle;

    public SecretVaultService() : this(() => DateTimeOffset.UtcNow) { }

    internal SecretVaultService(Func<DateTimeOffset> utcNow)
    {
        _utcNow = utcNow;
    }

    public static bool IsOtpHandle(string? value)
        => !string.IsNullOrWhiteSpace(value) && HandlePattern.IsMatch(value.Trim());

    /// <summary>
    /// Detects whether <paramref name="rawValue"/> is an <c>otp#xxxxxx</c> vault handle OR the stripped 6-char hex
    /// suffix of a known active/recently-consumed vault handle (e.g. if an LLM mistakenly stripped the <c>otp#</c> prefix).
    /// </summary>
    public bool TryMatchHandleOrBareSuffix(string? rawValue, out string matchedHandle)
    {
        matchedHandle = "";
        if (string.IsNullOrWhiteSpace(rawValue)) return false;
        var clean = rawValue.Trim();
        if (IsOtpHandle(clean))
        {
            matchedHandle = clean.ToLowerInvariant();
            return true;
        }
        if (clean.Length != 6) return false;

        var candidate = $"otp#{clean.ToLowerInvariant()}";
        if (!HandlePattern.IsMatch(candidate)) return false;

        var now = _utcNow();
        lock (_lock)
        {
            PruneExpiredLocked(now);
            if (_entries.ContainsKey(candidate) ||
                _recentlyConsumed.Any(c => string.Equals(c.Handle, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                matchedHandle = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Stores an OTP code in memory and returns an opaque handle (<c>otp#&lt;6 hex&gt;</c>).
    /// If the same <paramref name="mailId"/> already has an active unexpired handle for the same code, reuses that handle.
    /// </summary>
    public string StoreOtp(
        string rawCode,
        string? senderDomain = null,
        string? senderName = null,
        string? mailId = null,
        TimeSpan? ttl = null)
    {
        var clean = (rawCode ?? "").Trim();
        if (string.IsNullOrWhiteSpace(clean))
            throw new ArgumentException("OTP code cannot be empty.", nameof(rawCode));

        // Guard: NEVER double-vault an existing otp#xxxxxx handle or its stripped 6-hex suffix
        // (e.g. if an LLM stripped "otp#" from "otp#4a550d" and passed "4a550d" back as the code).
        if (TryMatchHandleOrBareSuffix(clean, out var existingMatchedHandle))
            return existingMatchedHandle;

        var effectiveTtl = ttl ?? DefaultTtl;
        if (effectiveTtl < TimeSpan.FromSeconds(1)) effectiveTtl = TimeSpan.FromSeconds(1);
        if (effectiveTtl > TimeSpan.FromMinutes(10)) effectiveTtl = TimeSpan.FromMinutes(10);

        var now = _utcNow();
        lock (_lock)
        {
            PruneExpiredLocked(now);

            if (!string.IsNullOrWhiteSpace(mailId))
            {
                if (_mailIdToHandle.TryGetValue(mailId, out var existingHandle) &&
                    _entries.TryGetValue(existingHandle, out var existing) &&
                    string.Equals(existing.Value, clean, StringComparison.Ordinal))
                {
                    _lastActiveHandle = existingHandle;
                    return existingHandle;
                }

                // Never resurrect an already-consumed OTP if delayed AI triage re-discovers the same (mailId, code).
                var alreadyConsumed = _recentlyConsumed.LastOrDefault(c =>
                    string.Equals(c.MailId, mailId, StringComparison.Ordinal) &&
                    string.Equals(c.Value, clean, StringComparison.Ordinal));
                if (alreadyConsumed is not null)
                    return alreadyConsumed.Handle;
            }

            Span<byte> buf = stackalloc byte[3];
            string handle;
            do
            {
                RandomNumberGenerator.Fill(buf);
                handle = $"otp#{Convert.ToHexString(buf).ToLowerInvariant()}";
            } while (!handle[4..].Any(c => c is >= 'a' and <= 'f') ||
                     _entries.ContainsKey(handle) ||
                     _recentlyConsumed.Any(c => string.Equals(c.Handle, handle, StringComparison.OrdinalIgnoreCase)));

            var meta = new SecretMetadata(
                Handle: handle,
                Kind: "otp",
                SenderDomain: senderDomain?.Trim().ToLowerInvariant(),
                SenderName: senderName?.Trim(),
                MailId: mailId,
                CreatedAt: now,
                ExpiresAt: now + effectiveTtl);

            _entries[handle] = new VaultEntry(clean, meta);
            if (!string.IsNullOrWhiteSpace(mailId))
                _mailIdToHandle[mailId] = handle;
            _lastActiveHandle = handle;
            return handle;
        }
    }

    public string? GetLatestActiveHandle()
    {
        var now = _utcNow();
        lock (_lock)
        {
            PruneExpiredLocked(now);
            if (!string.IsNullOrWhiteSpace(_lastActiveHandle) && _entries.ContainsKey(_lastActiveHandle))
                return _lastActiveHandle;
            return _entries.Values
                .OrderByDescending(e => e.Metadata.CreatedAt)
                .FirstOrDefault()?.Metadata.Handle;
        }
    }

    public bool TryPeekMetadata(string? handle, out SecretMetadata? metadata)
    {
        metadata = null;
        if (string.IsNullOrWhiteSpace(handle)) return false;

        var now = _utcNow();
        lock (_lock)
        {
            PruneExpiredLocked(now);
            if (_entries.TryGetValue(handle.Trim(), out var entry))
            {
                metadata = entry.Metadata;
                return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Resolves an active, unexpired OTP handle WITHOUT consuming it.
    /// Callers should call <see cref="MarkConsumed"/> only after the fill or copy operation succeeds
    /// so a failed fill never destroys the user's OTP handle.
    /// </summary>
    public bool TryResolve(string? handle, out string secretValue, out SecretMetadata? metadata)
    {
        secretValue = "";
        metadata = null;
        if (string.IsNullOrWhiteSpace(handle)) return false;

        var key = handle.Trim();
        var now = _utcNow();
        lock (_lock)
        {
            PruneExpiredLocked(now);
            if (!_entries.TryGetValue(key, out var entry))
                return false;

            secretValue = entry.Value;
            metadata = entry.Metadata;
            return true;
        }
    }

    /// <summary>
    /// Atomically marks an active OTP handle as consumed (removing it from the active vault and leaving
    /// an un-consumable redaction tombstone until <see cref="SecretMetadata.ExpiresAt"/>).
    /// Returns <c>true</c> if the handle was active and consumed; <c>false</c> if already consumed or expired.
    /// </summary>
    public bool MarkConsumed(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return false;

        var key = handle.Trim();
        var now = _utcNow();
        lock (_lock)
        {
            PruneExpiredLocked(now);
            if (!_entries.Remove(key, out var entry))
                return false;

            CleanupAndTombstoneLocked(key, entry);
            return true;
        }
    }

    public bool IsRecentlyConsumed(string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle)) return false;
        var key = handle.Trim();
        var now = _utcNow();
        lock (_lock)
        {
            PruneExpiredLocked(now);
            return _recentlyConsumed.Any(c => string.Equals(c.Handle, key, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// Single-use consumption: atomically removes the entry from the vault and returns its plaintext value.
    /// Any subsequent call with the same handle returns <c>false</c>.
    /// </summary>
    public bool TryConsume(string? handle, out string secretValue, out SecretMetadata? metadata)
    {
        secretValue = "";
        metadata = null;
        if (string.IsNullOrWhiteSpace(handle)) return false;

        var key = handle.Trim();
        var now = _utcNow();
        lock (_lock)
        {
            PruneExpiredLocked(now);
            if (!_entries.Remove(key, out var entry))
                return false;

            CleanupAndTombstoneLocked(key, entry);
            secretValue = entry.Value;
            metadata = entry.Metadata;
            return true;
        }
    }

    private void CleanupAndTombstoneLocked(string key, VaultEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Metadata.MailId) &&
            _mailIdToHandle.TryGetValue(entry.Metadata.MailId, out var mapped) &&
            string.Equals(mapped, key, StringComparison.OrdinalIgnoreCase))
        {
            _mailIdToHandle.Remove(entry.Metadata.MailId);
        }

        if (string.Equals(_lastActiveHandle, key, StringComparison.OrdinalIgnoreCase))
            _lastActiveHandle = null;

        // Keep an un-consumable redaction tombstone until ExpiresAt so delayed triage/snippets
        // still redact the plaintext code and cannot resurrect a consumed OTP.
        _recentlyConsumed.Add(new ConsumedRedactionEntry(entry.Value, entry.Metadata.Handle, entry.Metadata.MailId, entry.Metadata.ExpiresAt));
    }

    /// <summary>
    /// Redacts any currently vaulted OR recently consumed (still within TTL) plaintext OTP code from <paramref name="text"/>,
    /// replacing it with its opaque handle so plaintext codes never enter notifications, ChatMessages, Blackboard, or logs.
    /// </summary>
    public string RedactKnownSecrets(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";

        var now = _utcNow();
        List<(string Value, string Handle)> snapshot;
        lock (_lock)
        {
            PruneExpiredLocked(now);
            if (_entries.Count == 0 && _recentlyConsumed.Count == 0) return text;
            snapshot = _entries.Values
                .Select(e => (e.Value, e.Metadata.Handle))
                .Concat(_recentlyConsumed.Select(c => (c.Value, c.Handle)))
                .Distinct()
                .OrderByDescending(x => x.Value.Length)
                .ToList();
        }

        var output = text;
        foreach (var (val, handle) in snapshot)
        {
            if (val.Length >= 4 && output.Contains(val, StringComparison.Ordinal))
                output = output.Replace(val, handle, StringComparison.Ordinal);
        }
        return output;
    }

    private void PruneExpiredLocked(DateTimeOffset now)
    {
        _recentlyConsumed.RemoveAll(c => c.ExpiresAt <= now);
        if (_entries.Count == 0) return;
        List<string>? expired = null;
        foreach (var kv in _entries)
        {
            if (kv.Value.Metadata.ExpiresAt <= now)
                (expired ??= new List<string>()).Add(kv.Key);
        }
        if (expired is null) return;
        foreach (var key in expired)
        {
            if (_entries.Remove(key, out var removed) &&
                !string.IsNullOrWhiteSpace(removed.Metadata.MailId) &&
                _mailIdToHandle.TryGetValue(removed.Metadata.MailId, out var mapped) &&
                string.Equals(mapped, key, StringComparison.OrdinalIgnoreCase))
            {
                _mailIdToHandle.Remove(removed.Metadata.MailId);
            }
            if (string.Equals(_lastActiveHandle, key, StringComparison.OrdinalIgnoreCase))
                _lastActiveHandle = null;
        }
    }
}
