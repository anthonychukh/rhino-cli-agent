using System.Security.Cryptography;
using System.Text;

namespace RhinoAgent.Memory;

/// <summary>
/// Keeps a bounded, incremental index of completed conversation turns for one
/// RhinoAgent session. Only compact, visible turn data is retained; provider
/// prompts, hidden tool blocks, and tool output are intentionally excluded.
/// </summary>
public sealed class AgentConversationIndex
{
    public const int AutomaticFlushTurnCount = 4;
    public const int AutomaticFlushCharacterCount = 12000;
    public const int MaximumBatchTurnCount = 8;
    public const int MaximumBatchCharacterCount = 24000;
    public const int MaximumPendingTurnCount = 32;
    public const int MaximumPendingCharacterCount = 96000;

    private const int MaximumMessageCharacterCount = 6000;
    private const int MaximumSeenFingerprintCount = 256;

    private readonly List<AgentConversationTurn> _pending = [];
    private readonly HashSet<string> _seenFingerprints = new(StringComparer.Ordinal);
    private readonly Queue<string> _seenFingerprintOrder = [];
    private int _nextSequence = 1;
    private int _pendingCharacterCount;

    public int PendingTurnCount => _pending.Count;
    public int PendingCharacterCount => _pendingCharacterCount;
    public int DroppedTurnCount { get; private set; }
    public bool ShouldFlushAutomatically =>
        PendingTurnCount >= AutomaticFlushTurnCount
        || PendingCharacterCount >= AutomaticFlushCharacterCount;

    public bool TryAdd(string userMessage, AgentTurnResult result, out AgentConversationTurn turn)
    {
        turn = null!;
        if (!result.Success)
            return false;

        var user = NormalizeAndTruncate(userMessage);
        var assistant = NormalizeAndTruncate(result.VisibleText);
        if (user.Length == 0 && assistant.Length == 0)
            return false;

        var fingerprint = BuildFingerprint(
            user,
            assistant,
            result.ToolCallCount,
            result.ToolResultCount);
        if (!_seenFingerprints.Add(fingerprint))
            return false;

        _seenFingerprintOrder.Enqueue(fingerprint);
        while (_seenFingerprintOrder.Count > MaximumSeenFingerprintCount)
            _seenFingerprints.Remove(_seenFingerprintOrder.Dequeue());

        turn = new AgentConversationTurn(
            _nextSequence++,
            user,
            assistant,
            result.ToolCallCount,
            result.ToolResultCount,
            fingerprint);
        _pending.Add(turn);
        _pendingCharacterCount += turn.CharacterCount;
        TrimPendingQueue();
        return true;
    }

    public IReadOnlyList<AgentConversationTurn> GetNextBatch(int maximumTurnCount = MaximumBatchTurnCount)
    {
        if (_pending.Count == 0)
            return [];

        maximumTurnCount = Math.Clamp(maximumTurnCount, 1, MaximumBatchTurnCount);
        var batch = new List<AgentConversationTurn>();
        var characterCount = 0;
        foreach (var turn in _pending)
        {
            if (batch.Count >= maximumTurnCount)
                break;

            var nextCharacterCount = characterCount + turn.CharacterCount;
            if (batch.Count > 0 && nextCharacterCount > MaximumBatchCharacterCount)
                break;

            batch.Add(turn);
            characterCount = nextCharacterCount;
        }

        return batch;
    }

    public void MarkIndexed(IReadOnlyList<AgentConversationTurn> batch)
    {
        if (batch.Count == 0)
            return;

        if (batch.Count > _pending.Count)
            throw new InvalidOperationException("Conversation index batch contains more turns than the pending queue.");

        var count = batch.Count;
        for (var i = 0; i < count; i++)
        {
            if (!string.Equals(_pending[i].Fingerprint, batch[i].Fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException("Conversation index batches must be completed in queue order.");
        }

        for (var i = 0; i < count; i++)
            _pendingCharacterCount -= _pending[i].CharacterCount;
        _pending.RemoveRange(0, count);
    }

    public void RestoreBatch(IReadOnlyList<AgentConversationTurn> batch)
    {
        if (batch.Count == 0)
            return;

        _pending.InsertRange(0, batch);
        _pendingCharacterCount += batch.Sum(turn => turn.CharacterCount);

        // Keep the failed, older batch available for retry. If the bounded
        // queue overflowed while it was in flight, discard newest turns first.
        while (_pending.Count > MaximumPendingTurnCount
            || PendingCharacterCount > MaximumPendingCharacterCount)
        {
            var last = _pending[^1];
            _pendingCharacterCount -= last.CharacterCount;
            _pending.RemoveAt(_pending.Count - 1);
            DroppedTurnCount++;
        }
    }

    private void TrimPendingQueue()
    {
        while (_pending.Count > MaximumPendingTurnCount
            || PendingCharacterCount > MaximumPendingCharacterCount)
        {
            _pendingCharacterCount -= _pending[0].CharacterCount;
            _pending.RemoveAt(0);
            DroppedTurnCount++;
        }
    }

    private static string NormalizeAndTruncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Trim();
        if (normalized.Length <= MaximumMessageCharacterCount)
            return normalized;

        var omitted = normalized.Length - MaximumMessageCharacterCount;
        return $"{normalized[..MaximumMessageCharacterCount]}\n... truncated {omitted} characters";
    }

    private static string BuildFingerprint(
        string user,
        string assistant,
        int toolCallCount,
        int toolResultCount)
    {
        var canonical = string.Join('\u001f',
        [
            user,
            assistant,
            toolCallCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
            toolResultCount.ToString(System.Globalization.CultureInfo.InvariantCulture)
        ]);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}
