﻿using System;

namespace Reddit_Wallpaper_Changer
{
    public class DbRedditImage
    {
        public string Thumbnail { get; }
        public string Title { get; }
        public string ThreadId { get; }
        public string Url { get; }
        public DateTime DateTime { get; }

        public DbRedditImage(string thumbnail, string title, string threadId, string url, DateTime dateTime)
        {
            Thumbnail = thumbnail;
            Title = title;
            ThreadId = threadId;
            Url = url;
            DateTime = dateTime;
        }
    }
}
