using System.Buffers;
using System.Text;
using System.Text.Json;
using VigilWatt.Core.Utils;

namespace VigilWatt.Core.Capture;
/// <summary>
/// 功 能： 采集包的 JSON 读写契约。JSONL 每行一条，字段名改动即破坏历史包兼容。
/// 2026/10/24 12:40:43 20211
/// `dataBase64` 是规范来源，`dataHex` 只是派生展示；读回时以 base64 为准。
/// </summary>
public static class CaptureJson
{
    private const string DataDirRoot = "captures";

    public static string RecordLine(CaptureRecord record) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("tsMono", record.TimestampMono);
        w.WriteString("tsUtc", FormatUtc(record.TimestampUtc));
        w.WriteString("layer", record.Layer.ToWire());
        w.WriteString("source", record.Source);
        w.WriteString("deviceId", record.DeviceId);
        WriteOptional(w, "interfaceId", record.InterfaceId);
        w.WriteString("recordType", record.RecordType);
        if (record.ReportId is { } reportId) w.WriteNumber("reportId", reportId);
        WriteOptional(w, "api", record.Api);
        w.WriteNumber("length", record.Length);
        w.WriteString("dataBase64", Convert.ToBase64String(record.Data));
        w.WriteString("dataHex", record.DataHex);
        w.WriteString("status", record.Status.ToWire());
        WriteOptional(w, "error", record.Error);
        w.WriteEndObject();
    });

    public static bool TryParseRecord(string line, out CaptureRecord? record)
    {
        record = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            var data = root.TryGetProperty("dataBase64", out var b64)
                ? Convert.FromBase64String(b64.GetString() ?? "")
                : [];
            record = new CaptureRecord
            {
                TimestampMono = root.GetProperty("tsMono").GetInt64(),
                TimestampUtc = ParseUtc(root.GetProperty("tsUtc").GetString()),
                Layer = CaptureNames.ParseLayer(GetString(root, "layer")),
                Source = GetString(root, "source") ?? "",
                DeviceId = GetString(root, "deviceId") ?? "",
                InterfaceId = GetString(root, "interfaceId"),
                RecordType = GetString(root, "recordType") ?? "",
                ReportId = root.TryGetProperty("reportId", out var rid) ? rid.GetInt32() : null,
                Api = GetString(root, "api"),
                Data = data,
                Status = CaptureNames.ParseStatus(GetString(root, "status")),
                Error = GetString(root, "error"),
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Debug($"采集记录行解析失败: {ex.Message}");
            return false;
        }
    }

    public static string EventLine(CaptureEvent item) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("tsMono", item.TimestampMono);
        w.WriteString("tsUtc", FormatUtc(item.TimestampUtc));
        w.WriteString("kind", item.Kind.ToWire());
        WriteOptional(w, "note", item.Note);
        WriteOptional(w, "stepId", item.StepId);
        if (item.Percent is { } percent) w.WriteNumber("percent", percent);
        WriteOptional(w, "deviceId", item.DeviceId);
        w.WriteEndObject();
    });

    public static bool TryParseEvent(string line, out CaptureEvent? item)
    {
        item = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            item = new CaptureEvent
            {
                TimestampMono = root.GetProperty("tsMono").GetInt64(),
                TimestampUtc = ParseUtc(root.GetProperty("tsUtc").GetString()),
                Kind = CaptureNames.ParseEventKind(GetString(root, "kind")),
                Note = GetString(root, "note"),
                StepId = GetString(root, "stepId"),
                Percent = root.TryGetProperty("percent", out var pct) ? pct.GetInt32() : null,
                DeviceId = GetString(root, "deviceId"),
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Debug($"采集事件行解析失败: {ex.Message}");
            return false;
        }
    }

    public static string ErrorLine(CaptureError item) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("tsMono", item.TimestampMono);
        w.WriteString("tsUtc", FormatUtc(item.TimestampUtc));
        w.WriteString("kind", item.Kind.ToWire());
        w.WriteString("message", item.Message);
        if (item.HResult is { } hr) w.WriteNumber("hresult", hr);
        WriteOptional(w, "deviceId", item.DeviceId);
        WriteOptional(w, "interfaceId", item.InterfaceId);
        w.WriteEndObject();
    });

    public static bool TryParseError(string line, out CaptureError? item)
    {
        item = null;
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            item = new CaptureError
            {
                TimestampMono = root.GetProperty("tsMono").GetInt64(),
                TimestampUtc = ParseUtc(root.GetProperty("tsUtc").GetString()),
                Kind = CaptureNames.ParseErrorKind(GetString(root, "kind")),
                Message = GetString(root, "message") ?? "",
                HResult = root.TryGetProperty("hresult", out var hr) ? hr.GetInt32() : null,
                DeviceId = GetString(root, "deviceId"),
                InterfaceId = GetString(root, "interfaceId"),
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Debug($"采集错误行解析失败: {ex.Message}");
            return false;
        }
    }

    public static string SessionJson(CaptureSessionInfo info) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("id", info.Id);
        w.WriteString("task", info.Task.ToWire());
        w.WriteString("method", info.Method == CaptureMethod.UsbHid ? "usb-hid" : "unknown");
        w.WriteString("state", info.State.ToWire());
        w.WriteString("startedUtc", FormatUtc(info.StartedUtc));
        if (info.EndedUtc is { } ended) w.WriteString("endedUtc", FormatUtc(ended));
        if (info.DurationMs is { } ms) w.WriteNumber("durationMs", ms);
        w.WriteString("osVersion", info.OsVersion);
        w.WriteString("appVersion", info.AppVersion);
        w.WriteString("backend", info.Backend);
        WriteOptional(w, "deviceId", info.DeviceId);
        w.WriteBoolean("featurePollingEnabled", info.FeaturePollingEnabled);
        WriteByteArray(w, "featurePollReportIds", info.FeaturePollReportIds);
        w.WriteNumber("recordCount", info.RecordCount);
        w.WriteNumber("eventCount", info.EventCount);
        w.WriteNumber("errorCount", info.ErrorCount);
        w.WriteBoolean("recordsTruncated", info.RecordsTruncated);
        WriteOptional(w, "note", info.Note);

        w.WriteStartArray("capabilities");
        foreach (var cap in info.Capabilities) WriteCapability(w, cap);
        w.WriteEndArray();
        w.WriteEndObject();
    }, indented: true);

    public static bool TryParseSession(string json, out CaptureSessionInfo? info)
    {
        info = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var caps = new List<CapabilityItem>();
            if (root.TryGetProperty("capabilities", out var capArray) && capArray.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in capArray.EnumerateArray()) caps.Add(ReadCapability(element));
            }

            info = new CaptureSessionInfo
            {
                Id = GetString(root, "id") ?? "",
                StartedUtc = ParseUtc(GetString(root, "startedUtc")),
                OsVersion = GetString(root, "osVersion") ?? "",
                AppVersion = GetString(root, "appVersion") ?? "",
                Backend = GetString(root, "backend") ?? "",
                DeviceId = GetString(root, "deviceId"),
                FeaturePollingEnabled = root.TryGetProperty("featurePollingEnabled", out var fp) && fp.GetBoolean(),
                FeaturePollReportIds = ReadByteArray(root, "featurePollReportIds"),
                Capabilities = caps,
                Note = GetString(root, "note"),
            };
            info.State = CaptureNames.ParseState(GetString(root, "state"));
            info.Task = CaptureNames.ParseTask(GetString(root, "task"));
            if (root.TryGetProperty("endedUtc", out var ended)) info.EndedUtc = ParseUtc(ended.GetString());
            if (root.TryGetProperty("durationMs", out var ms)) info.DurationMs = ms.GetInt64();
            info.RecordCount = root.TryGetProperty("recordCount", out var rc) ? rc.GetInt32() : 0;
            info.EventCount = root.TryGetProperty("eventCount", out var ec) ? ec.GetInt32() : 0;
            info.ErrorCount = root.TryGetProperty("errorCount", out var erc) ? erc.GetInt32() : 0;
            info.RecordsTruncated = root.TryGetProperty("recordsTruncated", out var rt) && rt.GetBoolean();
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Debug($"session.json 解析失败: {ex.Message}");
            return false;
        }
    }

    public static string DeviceJson(CaptureDeviceInfo device) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteString("deviceId", device.DeviceId);
        w.WriteString("vidPid", device.VidPid);
        w.WriteNumber("vendorId", device.VendorId);
        w.WriteNumber("productId", device.ProductId);
        w.WriteNumber("versionNumber", device.VersionNumber);
        w.WriteString("product", device.Product);
        w.WriteString("manufacturer", device.Manufacturer);
        WriteOptional(w, "serialNumber", device.SerialNumber);
        w.WriteString("interfacePath", device.InterfacePath);
        w.WriteString("interfaceKey", device.InterfaceKey);
        w.WriteString("groupKey", device.GroupKey);
        w.WriteNumber("usagePage", device.UsagePage);
        w.WriteNumber("usage", device.Usage);
        w.WriteNumber("inputReportLength", device.InputReportLength);
        w.WriteNumber("outputReportLength", device.OutputReportLength);
        w.WriteNumber("featureReportLength", device.FeatureReportLength);
        WriteByteArray(w, "inputReportIds", device.InputReportIds);
        WriteByteArray(w, "outputReportIds", device.OutputReportIds);
        WriteByteArray(w, "featureReportIds", device.FeatureReportIds);
        w.WriteStartArray("fields");
        foreach (var field in device.Fields) WriteField(w, field);
        w.WriteEndArray();
        w.WriteBoolean("capsAvailable", device.CapsAvailable);
        w.WriteBoolean("accessDenied", device.AccessDenied);
        if (device.OpenError is { } err) w.WriteNumber("openError", err);
        w.WriteEndObject();
    }, indented: true);

    public static bool TryParseDevice(string json, out CaptureDeviceInfo? device)
    {
        device = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            device = new CaptureDeviceInfo
            {
                DeviceId = GetString(root, "deviceId") ?? "",
                VendorId = (ushort)root.GetProperty("vendorId").GetInt32(),
                ProductId = (ushort)root.GetProperty("productId").GetInt32(),
                VersionNumber = root.TryGetProperty("versionNumber", out var ver) ? (ushort)ver.GetInt32() : (ushort)0,
                Product = GetString(root, "product") ?? "",
                Manufacturer = GetString(root, "manufacturer") ?? "",
                SerialNumber = GetString(root, "serialNumber"),
                InterfacePath = GetString(root, "interfacePath") ?? "",
                InterfaceKey = GetString(root, "interfaceKey") ?? "",
                GroupKey = GetString(root, "groupKey") ?? "",
                UsagePage = root.TryGetProperty("usagePage", out var up) ? (ushort)up.GetInt32() : (ushort)0,
                Usage = root.TryGetProperty("usage", out var us) ? (ushort)us.GetInt32() : (ushort)0,
                InputReportLength = root.TryGetProperty("inputReportLength", out var ir) ? (ushort)ir.GetInt32() : (ushort)0,
                OutputReportLength = root.TryGetProperty("outputReportLength", out var orl) ? (ushort)orl.GetInt32() : (ushort)0,
                FeatureReportLength = root.TryGetProperty("featureReportLength", out var fr) ? (ushort)fr.GetInt32() : (ushort)0,
                InputReportIds = ReadByteArray(root, "inputReportIds"),
                OutputReportIds = ReadByteArray(root, "outputReportIds"),
                FeatureReportIds = ReadByteArray(root, "featureReportIds"),
                Fields = ReadFields(root, "fields"),
                CapsAvailable = root.TryGetProperty("capsAvailable", out var capsOk) && capsOk.GetBoolean(),
                AccessDenied = root.TryGetProperty("accessDenied", out var ad) && ad.GetBoolean(),
                OpenError = root.TryGetProperty("openError", out var oe) ? oe.GetInt32() : null,
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Debug($"device.json 解析失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>采集包目录名：captures 在数据目录下，与 settings.json / history.db 平级。</summary>
    public static string CapturesRoot => Path.Combine(AppPaths.RootDir, DataDirRoot);

    public static string AnalysisJson(CaptureAnalysis analysis) => Write(w =>
    {
        w.WriteStartObject();
        w.WriteNumber("totalRecords", analysis.TotalRecords);
        w.WriteNumber("receivedRecords", analysis.ReceivedRecords);
        w.WriteNumber("failedRecords", analysis.FailedRecords);
        w.WriteNumber("totalEvents", analysis.TotalEvents);
        w.WriteNumber("totalErrors", analysis.TotalErrors);
        w.WriteNumber("totalBytes", analysis.TotalBytes);

        w.WriteStartArray("groups");
        foreach (var group in analysis.Groups)
        {
            w.WriteStartObject();
            w.WriteString("key", group.Key);
            w.WriteString("recordType", group.RecordType);
            if (group.ReportId is { } rid) w.WriteNumber("reportId", rid);
            w.WriteNumber("length", group.Length);
            w.WriteStartArray("lengths");
            foreach (var length in group.Lengths) w.WriteNumberValue(length);
            w.WriteEndArray();
            w.WriteNumber("count", group.Count);
            w.WriteNumber("distinctPayloads", group.DistinctPayloads);
            w.WriteNumber("duplicateRatio", Math.Round(group.DuplicateRatio, 4));
            w.WriteString("firstSeenUtc", FormatUtc(group.FirstSeenUtc));
            w.WriteString("lastSeenUtc", FormatUtc(group.LastSeenUtc));
            w.WriteString("firstHex", group.FirstHex);
            w.WriteString("lastHex", group.LastHex);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("changes");
        foreach (var change in analysis.Changes)
        {
            w.WriteStartObject();
            w.WriteString("groupKey", change.GroupKey);
            w.WriteNumber("offset", change.Offset);
            if (change.From is { } from) w.WriteNumber("from", from);
            if (change.To is { } to) w.WriteNumber("to", to);
            w.WriteNumber("occurrences", change.Occurrences);
            w.WriteBoolean("lengthChanged", change.LengthChanged);
            w.WriteNumber("fromLength", change.FromLength);
            w.WriteNumber("toLength", change.ToLength);
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("marks");
        foreach (var mark in analysis.Marks)
        {
            w.WriteStartObject();
            w.WriteString("note", mark.Note);
            w.WriteNumber("tsMono", mark.TimestampMono);
            w.WriteString("tsUtc", FormatUtc(mark.TimestampUtc));
            w.WriteNumber("recordsBefore", mark.RecordsBefore);
            w.WriteNumber("recordsInStep", mark.RecordsInStep);
            w.WriteStartArray("newGroupsInStep");
            foreach (var key in mark.NewGroupsInStep) w.WriteStringValue(key);
            w.WriteEndArray();
            w.WriteStartArray("changes");
            foreach (var change in mark.Changes)
            {
                w.WriteStartObject();
                w.WriteString("groupKey", change.GroupKey);
                w.WriteNumber("offset", change.Offset);
                if (change.From is { } from) w.WriteNumber("from", from);
                if (change.To is { } to) w.WriteNumber("to", to);
                w.WriteNumber("occurrences", change.Occurrences);
                w.WriteBoolean("lengthChanged", change.LengthChanged);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();

        w.WriteStartArray("batteryReferences");
        foreach (var reference in analysis.BatteryReferences)
        {
            w.WriteStartObject();
            w.WriteNumber("percent", reference.Percent);
            w.WriteNumber("tsMono", reference.TimestampMono);
            w.WriteString("tsUtc", FormatUtc(reference.TimestampUtc));
            WriteOptional(w, "nextRecordHex", reference.NextRecordHex);
            if (reference.NextRecordMono is { } nextMono) w.WriteNumber("nextRecordMono", nextMono);
            w.WriteStartArray("matchingOffsets");
            foreach (var offset in reference.MatchingOffsets) w.WriteNumberValue(offset);
            w.WriteEndArray();
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteEndObject();
    }, indented: true);

    private static void WriteField(Utf8JsonWriter w, HidFieldInfo field)
    {
        w.WriteStartObject();
        w.WriteString("reportType", field.ReportType);
        if (field.ReportId is { } id) w.WriteNumber("reportId", id);
        w.WriteNumber("usagePage", field.UsagePage);
        w.WriteNumber("usageMin", field.UsageMin);
        w.WriteNumber("usageMax", field.UsageMax);
        w.WriteNumber("bitSize", field.BitSize);
        w.WriteNumber("reportCount", field.ReportCount);
        w.WriteNumber("logicalMin", field.LogicalMin);
        w.WriteNumber("logicalMax", field.LogicalMax);
        w.WriteBoolean("isAbsolute", field.IsAbsolute);
        w.WriteBoolean("isButton", field.IsButton);
        w.WriteEndObject();
    }

    private static List<HidFieldInfo> ReadFields(JsonElement root, string name)
    {
        var list = new List<HidFieldInfo>();
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return list;
        foreach (var element in array.EnumerateArray())
        {
            list.Add(new HidFieldInfo
            {
                ReportType = GetString(element, "reportType") ?? "input",
                ReportId = element.TryGetProperty("reportId", out var id) ? id.GetInt32() : null,
                UsagePage = (ushort)element.GetProperty("usagePage").GetInt32(),
                UsageMin = (ushort)element.GetProperty("usageMin").GetInt32(),
                UsageMax = (ushort)element.GetProperty("usageMax").GetInt32(),
                BitSize = element.GetProperty("bitSize").GetInt32(),
                ReportCount = element.GetProperty("reportCount").GetInt32(),
                LogicalMin = element.GetProperty("logicalMin").GetInt32(),
                LogicalMax = element.GetProperty("logicalMax").GetInt32(),
                IsAbsolute = element.TryGetProperty("isAbsolute", out var abs) && abs.GetBoolean(),
                IsButton = element.TryGetProperty("isButton", out var btn) && btn.GetBoolean(),
            });
        }
        return list;
    }

    private static void WriteCapability(Utf8JsonWriter w, CapabilityItem cap)
    {
        w.WriteStartObject();
        w.WriteString("id", cap.Id);
        w.WriteString("title", cap.Title);
        w.WriteString("status", cap.Status.ToWire());
        WriteOptional(w, "evidence", cap.Evidence);
        WriteOptional(w, "reason", cap.Reason);
        WriteOptional(w, "impact", cap.Impact);
        WriteOptional(w, "nextStep", cap.NextStep);
        w.WriteEndObject();
    }

    private static CapabilityItem ReadCapability(JsonElement element) => new()
    {
        Id = GetString(element, "id") ?? "",
        Title = GetString(element, "title") ?? "",
        Status = CaptureNames.ParseCapabilityStatus(GetString(element, "status")),
        Evidence = GetString(element, "evidence"),
        Reason = GetString(element, "reason"),
        Impact = GetString(element, "impact"),
        NextStep = GetString(element, "nextStep"),
    };

    private static string Write(Action<Utf8JsonWriter> body, bool indented = false)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = indented }))
        {
            body(writer);
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteOptional(Utf8JsonWriter w, string name, string? value)
    {
        if (!string.IsNullOrEmpty(value)) w.WriteString(name, value);
    }

    private static void WriteByteArray(Utf8JsonWriter w, string name, IReadOnlyList<byte> values)
    {
        w.WriteStartArray(name);
        foreach (var value in values) w.WriteNumberValue(value);
        w.WriteEndArray();
    }

    private static List<byte> ReadByteArray(JsonElement root, string name)
    {
        var list = new List<byte>();
        if (!root.TryGetProperty(name, out var array) || array.ValueKind != JsonValueKind.Array) return list;
        foreach (var element in array.EnumerateArray()) list.Add((byte)element.GetInt32());
        return list;
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>统一 UTC 毫秒精度，避免历史包与 history.db 的时间基准对不齐。</summary>
    private static string FormatUtc(DateTimeOffset value)
        => value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    private static DateTimeOffset ParseUtc(string? text)
        => DateTimeOffset.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : DateTimeOffset.UnixEpoch;
}
