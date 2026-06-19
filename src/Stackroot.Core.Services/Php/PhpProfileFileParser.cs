using System.Text.Json;
using Stackroot.Core.Abstractions.DataDocuments;
using Stackroot.Core.IO;

namespace Stackroot.Core.Services.Php;

public static class PhpProfileFileParser
{
    public static IReadOnlyList<PhpProfileDocument> ParseProfiles(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Profile file is not valid JSON: {ex.Message}", ex);
        }

        using (document)
        {
            if (document.RootElement.TryGetProperty("profiles", out var profilesElement)
                && profilesElement.ValueKind == JsonValueKind.Array)
            {
                return ParseBundle(json);
            }
        }

        return [ParseSingle(json)];
    }

    public static PhpProfileDocument ParseSingle(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        PhpProfileDocument document;
        try
        {
            document = JsonSerializer.Deserialize<PhpProfileDocument>(json, JsonSerializerConfig.Default)
                       ?? throw new InvalidDataException("Profile file is empty or invalid JSON.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Profile file is not valid JSON: {ex.Message}", ex);
        }

        ValidateSingle(document);
        return document;
    }

    private static IReadOnlyList<PhpProfileDocument> ParseBundle(string json)
    {
        PhpProfilesBundleDocument bundle;
        try
        {
            bundle = JsonSerializer.Deserialize<PhpProfilesBundleDocument>(json, JsonSerializerConfig.Default)
                     ?? throw new InvalidDataException("Profile bundle is empty or invalid JSON.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Profile bundle is not valid JSON: {ex.Message}", ex);
        }

        if (bundle.SchemaVersion != DataDocumentSchemas.PhpProfilesBundle)
        {
            throw new InvalidDataException(
                $"Unsupported bundle schema version {bundle.SchemaVersion}. Expected {DataDocumentSchemas.PhpProfilesBundle}.");
        }

        if (bundle.Profiles.Count == 0)
        {
            throw new InvalidDataException("Profile bundle does not contain any PHP profiles.");
        }

        foreach (var profile in bundle.Profiles)
        {
            ValidateSingle(profile);
        }

        return bundle.Profiles;
    }

    private static void ValidateSingle(PhpProfileDocument document)
    {
        if (document.SchemaVersion != DataDocumentSchemas.PhpProfile)
        {
            throw new InvalidDataException(
                $"Unsupported profile schema version {document.SchemaVersion}. Expected {DataDocumentSchemas.PhpProfile}.");
        }

        if (string.IsNullOrWhiteSpace(document.TargetVersionId))
        {
            throw new InvalidDataException("Profile is missing targetVersionId.");
        }
    }
}
