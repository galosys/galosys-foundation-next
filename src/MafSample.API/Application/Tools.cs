using System.ComponentModel;

namespace MafSample.API.Application
{
    public class Tools
    {
        [Description("获取给定城市的天气")]
        public static string GetWeather([Description("要获取天气的城市")] string localtion)
        {
            return $"{localtion}的天气：🌤,15℃";
        }
    }
}
