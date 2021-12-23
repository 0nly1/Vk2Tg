using System;

namespace Config
{
    public class Settings
    {
        // bot token
        public static string TgToken = "";

        // vk user token
        public static string VkToken = "";
        public static ulong VkAppId = 0;

        // Telegram target channel name (It doesn't work with private channels. Will add it later)
        public static string TgTargetChatName = "";
        
        // Vk target group Id (will add searching by link later)
        public static int VkGroupId = 0;
    }
}