namespace RhinoAgent.Runtime;

internal sealed record ModelNameValidationResult(
    bool IsValid,
    string? CanonicalName,
    string? Suggestion);

internal static class ModelNameValidator
{
    public static ModelNameValidationResult Validate(
        string requestedName,
        IReadOnlyList<string> availableModels)
    {
        var models = availableModels
            .Where(model => !string.IsNullOrWhiteSpace(model))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var exact = models.FirstOrDefault(model =>
            string.Equals(model, requestedName, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return new ModelNameValidationResult(true, exact, null);

        if (models.Length == 0)
            return new ModelNameValidationResult(false, null, null);

        var normalizedRequested = NormalizeForDistance(requestedName);
        var suggestion = models
            .Select(model => new
            {
                Model = model,
                NormalizedDistance = EditDistance(normalizedRequested, NormalizeForDistance(model)),
                LiteralDistance = EditDistance(requestedName.ToLowerInvariant(), model.ToLowerInvariant())
            })
            .OrderBy(candidate => candidate.NormalizedDistance)
            .ThenBy(candidate => candidate.LiteralDistance)
            .ThenBy(candidate => candidate.Model, StringComparer.OrdinalIgnoreCase)
            .First()
            .Model;

        return new ModelNameValidationResult(false, null, suggestion);
    }

    private static string NormalizeForDistance(string value) =>
        new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());

    private static int EditDistance(string left, string right)
    {
        var distance = new int[left.Length + 1, right.Length + 1];
        for (var i = 0; i <= left.Length; i++)
            distance[i, 0] = i;
        for (var j = 0; j <= right.Length; j++)
            distance[0, j] = j;

        for (var i = 1; i <= left.Length; i++)
        {
            for (var j = 1; j <= right.Length; j++)
            {
                var substitutionCost = left[i - 1] == right[j - 1] ? 0 : 1;
                distance[i, j] = Math.Min(
                    Math.Min(distance[i - 1, j] + 1, distance[i, j - 1] + 1),
                    distance[i - 1, j - 1] + substitutionCost);

                if (i > 1
                    && j > 1
                    && left[i - 1] == right[j - 2]
                    && left[i - 2] == right[j - 1])
                {
                    distance[i, j] = Math.Min(distance[i, j], distance[i - 2, j - 2] + 1);
                }
            }
        }

        return distance[left.Length, right.Length];
    }
}
