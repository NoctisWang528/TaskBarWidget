using VigilWatt.Core.Hid;

namespace VigilWatt.Core.Capture.Hid;
/// <summary>
/// 功 能： 把采集用的设备信息转成 HID 层句柄工厂需要的元数据。
/// 2026/10/24 12:40:43 20211
/// </summary>
internal static class CaptureDeviceExtensions
{
    public static HidDeviceInfo ToHidDeviceInfo(this CaptureDeviceInfo device) => new()
    {
        DevicePath = device.InterfacePath,
        VendorId = device.VendorId,
        ProductId = device.ProductId,
        Version = device.VersionNumber,
        UsagePage = device.UsagePage,
        Usage = device.Usage,
        InputReportLength = device.InputReportLength,
        OutputReportLength = device.OutputReportLength,
        FeatureReportLength = device.FeatureReportLength,
        Product = device.Product,
        Manufacturer = device.Manufacturer,
        SerialNumber = device.SerialNumber ?? "",
    };
}
