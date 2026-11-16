using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BraveBackup.Extensions.TabManagerPlus;

/// <summary>Validated portable saved windows and their observed capture provenance, without source paths.</summary>
public sealed record SavedWindowPayloadData(SavedWindowCollection Data, DateTimeOffset CapturedUtc, int SourceFileCount);

/// <summary>Application-owned version-1 saved-window JSON, independent of browser storage and archive containers.</summary>
public static class SavedWindowPayload
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Serializes only approved fields with bounded output, then validates the resulting projection.</summary>
    public static byte[] Serialize(SavedWindowCollection data, DateTimeOffset capturedUtc, int sourceFileCount,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        using var output = new LimitedBuffer();
        JsonSerializer.Serialize(output, new
        {
            schemaVersion = 1, captureKind = "extension-saved-windows", capturedUtc,
            extensionId = SavedWindows.OfficialExtensionId, extensionVersion = data.ExtensionVersion,
            source = "local-extension-settings", sourceFileCount,
            windows = data.Windows.Select(window => new
            {
                window.Id, window.Name, window.Color, window.Date, window.SessionStartTime, window.Updated,
                window.Order, window.CustomName, window.Incognito, windowsInfo = window.Geometry, window.Tabs
            })
        }, Options);
        var bytes = output.ToArray();
        try { Parse(bytes, token); return bytes; }
        catch { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); throw; }
    }

    /// <summary>Validates exact supported provenance, all saved-window fields and canonical window ordering.</summary>
    public static SavedWindowPayloadData Parse(ReadOnlyMemory<byte> bytes, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > MaximumBytes) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() != root.EnumerateObject().Count()) throw Invalid();
            var envelope = root.Deserialize<Envelope>(Options);
            if (envelope is null || envelope.SchemaVersion != 1 || envelope.CaptureKind != "extension-saved-windows" ||
                envelope.CapturedUtc == default || envelope.CapturedUtc.Offset != TimeSpan.Zero ||
                envelope.ExtensionId != SavedWindows.OfficialExtensionId ||
                envelope.ExtensionVersion != SavedWindows.SupportedVersion || envelope.Source != "local-extension-settings" ||
                envelope.SourceFileCount is < 1 or > 4096 || envelope.Windows is null ||
                envelope.Windows.Length > SavedWindows.MaximumWindows) throw Invalid();
            using var map = new LimitedBuffer();
            using (var writer = new Utf8JsonWriter(map, new JsonWriterOptions { Encoder = Options.Encoder }))
            {
                writer.WriteStartObject();
                foreach (var window in envelope.Windows)
                {
                    token.ThrowIfCancellationRequested();
                    if (window.ValueKind != JsonValueKind.Object || !window.TryGetProperty("id", out var id) ||
                        id.ValueKind != JsonValueKind.String || id.GetString()!.Length > 512) throw Invalid();
                    writer.WritePropertyName(id.GetString()!);
                    window.WriteTo(writer);
                }
                writer.WriteEndObject();
            }
            var source = map.ToArray();
            try
            {
                var parsed = SavedWindows.Parse(source, envelope.ExtensionVersion, token);
                if (!parsed.Windows.Select(w => w.Id).SequenceEqual(envelope.Windows.Select(w => w.GetProperty("id").GetString())))
                    throw Invalid();
                return new SavedWindowPayloadData(parsed, envelope.CapturedUtc, envelope.SourceFileCount);
            }
            finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(source); }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException) { throw Invalid(); }
    }

    private sealed record Envelope(int SchemaVersion, string CaptureKind, DateTimeOffset CapturedUtc,
        string ExtensionId, string ExtensionVersion, string Source, int SourceFileCount, JsonElement[] Windows);

    private sealed class LimitedBuffer : MemoryStream
    {
        public override void Write(byte[] buffer, int offset, int count)
        {
            if (Position + count > MaximumBytes) throw Invalid();
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (Position + buffer.Length > MaximumBytes) throw Invalid();
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            if (Position == MaximumBytes) throw Invalid();
            base.WriteByte(value);
        }
    }

    private static InvalidDataException Invalid() => new("Invalid or unsupported portable saved-window payload.");
}
