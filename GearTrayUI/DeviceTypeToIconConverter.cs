using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using GearTray.Contracts;

namespace GearTrayUI;

public class DeviceTypeToIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        DeviceType type = DeviceType.Generic;
        bool isDefault = false;

        if (value is DeviceStatusEventArgs args)
        {
            type = args.Type;
            isDefault = args.IsDefault;
        }
        else if (value is DeviceViewModel vm)
        {
            type = vm.Type;
            isDefault = vm.IsDefault;
        }
        else if (value is DeviceType t)
        {
            type = t;
        }

        if (type == DeviceType.AudioOutput)
        {
            if (isDefault)
            {
                // Volume High / Speaker On (no strikethrough)
                return Geometry.Parse("M14,3.23V5.29C16.89,6.15 19,8.83 19,12C19,15.17 16.89,17.85 14,18.71V20.77C18,19.86 21,16.28 21,12C21,7.72 18,4.14 14,3.23M16.5,12C16.5,10.23 15.5,8.71 14,7.97V16C15.5,15.29 16.5,13.77 16.5,12M3,9V15H7L12,20V4L7,9H3Z");
            }
            else
            {
                // Speaker Muted/Off (with strikethrough)
                return Geometry.Parse("M12,4L9.91,6.09L12,8.18M4.27,3L3,4.27L7.73,9H3V15H7L12,20V13.27L16.25,17.53C15.58,18.04 14.83,18.46 14,18.7V20.77C15.38,20.44 16.63,19.79 17.68,18.95L19.73,21L21,19.73M19,12C19,12.94 18.8,13.82 18.46,14.64L19.97,16.15C20.62,14.91 21,13.5 21,12C21,7.27 17.64,3.32 13,2.41V4.49C16.5,5.36 19,8.38 19,12M16.5,12C16.5,10.29 15.5,8.82 14,8.12V10.18L16.45,12.63C16.48,12.42 16.5,12.21 16.5,12Z");
            }
        }
        else if (type == DeviceType.Headset)
        {
            if (isDefault)
            {
                // Headset On (no strikethrough)
                return Geometry.Parse("M12,2A9,9 0 0,0 3,11V18A3,3 0 0,0 6,21H8V14H5V11A7,7 0 0,1 12,4A7,7 0 0,1 19,11V14H16V21H18A3,3 0 0,0 21,18V11A9,9 0 0,0 12,2M7,16H6A1,1 0 0,1 5,15V15A1,1 0 0,1 6,14H7V16M17,16H18A1,1 0 0,1 19,15V15A1,1 0 0,1 18,14H17V16Z");
            }
            else
            {
                // Headset Off/Muted (with strikethrough diagonal stroke)
                return Geometry.Parse("M12,2A9,9 0 0,0 3,11V18A3,3 0 0,0 6,21H8V14H5V11A7,7 0 0,1 12,4A7,7 0 0,1 19,11V14H16V21H18A3,3 0 0,0 21,18V11A9,9 0 0,0 12,2M7,16H6A1,1 0 0,1 5,15V15A1,1 0 0,1 6,14H7V16M17,16H18A1,1 0 0,1 19,15V15A1,1 0 0,1 18,14H17V16Z M3,4.5 L4.5,3 L21,19.5 L19.5,21 Z");
            }
        }

        return type switch
        {
            DeviceType.Mouse => Geometry.Parse("M12,2A6,6 0 0,0 6,8V16A6,6 0 0,0 12,22A6,6 0 0,0 18,16V8A6,6 0 0,0 12,2M12,4A2,2 0 0,1 14,6H10A2,2 0 0,1 12,4M16,16A4,4 0 0,1 12,20A4,4 0 0,1 8,16V10H16V16Z"),
            DeviceType.Keyboard => Geometry.Parse("M20,5 H4 C2.9,5 2,5.9 2,7 V17 C2,18.1 2.9,19 4,19 H20 C21.1,19 22,18.1 22,17 V7 C22,5.9 21.1,5 20,5 Z M11,8 H13 V10 H11 V8 Z M11,11 H13 V13 H11 V11 Z M8,8 H10 V10 H8 V8 Z M8,11 H10 V13 H8 V11 Z M5,11 H7 V13 H5 V11 Z M5,8 H7 V10 H5 V8 Z M17,15 H7 V13 H17 V15 Z M17,11 H15 V13 H15 V11 Z M17,8 H15 V10 H15 V8 Z M20,15 H18 V13 H20 V15 Z M20,11 H18 V9 H20 V11 Z"),
            DeviceType.Controller => Geometry.Parse("M12 2C6.48 2 2 6.48 2 12s4.48 10 10 10 10-4.48 10-10S17.52 2 12 2zm1 10h3v2h-3v3h-2v-3H8v-2h3V9h2v3zm4.5 5.5c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5z"),
            _ => Geometry.Parse("M12,2C6.47,2 2,6.5 2,12C2,17.5 6.47,22 12,22C17.52,22 22,17.5 22,12C22,6.5 17.52,2 12,2M12,17C11.45,17 11,16.55 11,16C11,15.45 11.45,15 12,15C12.55,15 13,15.45 13,16C13,16.55 12.55,17 12,17M13,13H11V7H13V13Z")
        };
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotImplementedException();
}
