using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NyaaDownloader
{
    public class EpisodeData
    {
        public readonly string Name;
        public readonly string TorrentUrl;
        
        public EpisodeData(string name, string torrentUrl)
        {
            Name = name;
            TorrentUrl = torrentUrl;
        }
    }

    public class ShowData
    {
        public readonly string ShowName; // pretty printed with extension, e.g. "Initial D Fifth Stage - 02.mkv"
        public readonly string Subber;
        public readonly string Resolution;
        public readonly List<EpisodeData> Episodes = new List<EpisodeData>(); 

        public ShowData(string showName, string subber, string resolution)
        {
            ShowName = showName;
            Subber = subber;
            Resolution = resolution;
        }
    }
    
    public class Program
    {
        public static string GetWebPageContents(string url)
        {
            var pageContents = string.Empty;

            try
            {
                pageContents = new WebClient().DownloadString(url);
            }
            catch (Exception exception)
            {
                Console.WriteLine("Error: exception for specified URL ({0})", url);
            }

            return pageContents;
        }

        public static string BuildUrl(ShowData showData)
        {
            return "http://www.nyaa.se/?term=" +
                   "%5B" + showData.Subber + "%5D+" + showData.ShowName.Replace(' ', '+') + "+" + showData.Resolution;
        }

        public static void GetEpisodes(string pageContents, ShowData showData)
        {
            showData.Episodes.Clear();
            
            var tlistnameIndices = Regex.Matches(pageContents, "<td class=\"tlistname\">")
                .Cast<Match>().Select(match => match.Index).OrderBy(x => x).ToArray();

            var tlistdownloadIndices = Regex.Matches(pageContents, "<td class=\"tlistdownload\">")
                .Cast<Match>().Select(match => match.Index).OrderBy(x => x).ToArray();

            if (tlistnameIndices.Length != tlistdownloadIndices.Length)
            {
                Console.WriteLine("Error: parser error - number of tlistname and tlistdownload matches not equal ({0} vs {1})", tlistnameIndices.Count(), tlistdownloadIndices.Count());
                return;
            }

            for (var i = 0; i < tlistnameIndices.Length; i++)
            {
                if (tlistnameIndices[i] >= tlistdownloadIndices[i])
                {
                    Console.WriteLine("Error: parser error - tlistname index greater than tlistdownload index ({0} vs {1})", tlistnameIndices[i], tlistdownloadIndices[i]);
                    continue;
                }

                var rawNameBeginIndex = pageContents.IndexOf(">",
                                                             tlistnameIndices[i] + "<td class=\"tlistname\">".Length + 1,
                                                             tlistdownloadIndices[i] - tlistnameIndices[i],
                                                             StringComparison.Ordinal) + 1;

                var rawNameEndIndex = pageContents.IndexOf("</a>",
                                                           tlistnameIndices[i],
                                                           tlistdownloadIndices[i] - tlistnameIndices[i],
                                                           StringComparison.Ordinal);

                if (rawNameBeginIndex >= rawNameEndIndex || rawNameBeginIndex < 0 || rawNameEndIndex < 0)
                {
                    Console.WriteLine("Error: parser error - rawNameBeginIndex and/or rawNameEndIndex incorrect ({0}, {1})", rawNameBeginIndex, rawNameEndIndex);
                    continue;
                }

                var rawName = pageContents.Substring(rawNameBeginIndex, rawNameEndIndex - rawNameBeginIndex);

                if (!rawName.Contains(showData.Subber) || !rawName.Contains(showData.Resolution))
                {
                    continue;
                }
                
                if (!rawName.ToLower().EndsWith(".mp4") && !rawName.ToLower().EndsWith(".mkv"))
                {
                    continue; // avoid stuff like TV batches
                }

                var prettyName = PrettyPrintName(rawName);

                if (!prettyName.Contains(showData.ShowName))
                {
                    continue;
                }

                var torrentUrlBeginIndex = pageContents.IndexOf("<a href=\"",
                                                                 tlistdownloadIndices[i],
                                                                 i != tlistnameIndices.Length - 1
                                                                     ? tlistnameIndices[i + 1] - tlistdownloadIndices[i]
                                                                     : pageContents.Length - tlistdownloadIndices[i],
                                                                 StringComparison.Ordinal) + "<a href=\"".Length;

                var torrentUrlEndIndex = pageContents.IndexOf("\" title=\"Download\"",
                                                               tlistdownloadIndices[i],
                                                               i != tlistnameIndices.Length - 1
                                                                   ? tlistnameIndices[i + 1] - tlistdownloadIndices[i]
                                                                   : pageContents.Length - tlistdownloadIndices[i],
                                                               StringComparison.Ordinal);

                if (torrentUrlBeginIndex >= torrentUrlEndIndex || torrentUrlBeginIndex < 0 || torrentUrlEndIndex < 0)
                {
                    Console.WriteLine("Error: parser error - torrentUrlBeginIndex and/or torrentLinkEndIndex incorrect ({0}, {1})", torrentUrlBeginIndex, torrentUrlEndIndex);
                    continue;
                }

                var torrentUrl = pageContents.Substring(torrentUrlBeginIndex, torrentUrlEndIndex - torrentUrlBeginIndex).Replace("&#38;", "&");

                showData.Episodes.Add(new EpisodeData(prettyName, torrentUrl));
            }

            showData.Episodes.Reverse(); // oldest first
        }

        public static string PrettyPrintName(string rawName)
        {
            var prettyName = rawName;

            // Remove subber-specific format strings like "(720p)" and "(1280x720 Hi10P AAC)", 
            // will probably need to add to this in the future
            prettyName = Regex.Replace(prettyName, "\\([0-9]{3,4}x[0-9]{3,4}[^)]+\\)", string.Empty);
            prettyName = Regex.Replace(prettyName, "\\(720p\\)", string.Empty);
            
            prettyName = Regex.Replace(prettyName, "_", " ");
            prettyName = Regex.Replace(prettyName, "[ ]*\\[[^[]*\\][ ]*", string.Empty);

            return prettyName;
        }

        static void Main(string[] args)
        {
            // This prevents the framework from trying to autodetect proxy settings which can make the request
            // extremely slow (up to an additional 30 seconds).
            // http://www.nullskull.com/a/848/webclient-class-gotchas-and-basics.aspx
            WebRequest.DefaultWebProxy = null;

            if (args.Length != 4)
            {
                Console.WriteLine("Usage: NyaaDownloader.exe <SHOW NAME> <SUBBER> <RESOLUTION> <DOWNLOAD DIRECTORY>");
                Console.WriteLine("Example: NyaaDownloader.exe \"Sword Art Online II\" \"HorribleSubs\" \"480\" \"C:\\Downloads\"");
                Environment.Exit(0);
            }

            var showName = args[0].Trim();
            var subber = args[1].Trim();
            var resolution = args[2].Trim();
            var downloadDirectory = args[3].Trim();

            if (showName == string.Empty || subber == string.Empty || resolution == string.Empty || downloadDirectory == string.Empty)
            {
                Console.WriteLine("Error: none of the arguments can be empty, exiting...");
                Environment.Exit(0);
            }

            if (!Directory.Exists(downloadDirectory))
            {
                Console.WriteLine("Error: the specified directory does not exist, exiting...");
                Environment.Exit(0);
            }

            Console.WriteLine("\nWill now monitor and download for...");
            Console.WriteLine("   Show: {0}", showName);
            Console.WriteLine("   Subber: {0}", subber);
            Console.WriteLine("   Resolution: {0}", resolution);
            Console.WriteLine("Press any key to continue");

            Console.ReadKey(true);

            Console.WriteLine("\nLink Start!\n");

            var showData = new ShowData(showName.Trim(), subber.Trim(), resolution.Trim());

            var url = BuildUrl(showData);

            var downloadProcessStartInfo = new ProcessStartInfo {FileName = "aria2c.exe"};

            while (true)
            {
                var existingEpisodes = new string[0];
                try
                {
                    existingEpisodes = Directory.GetFiles(downloadDirectory)
                        .Select(Path.GetFileName)
                        .Where(x => x.ToLower().EndsWith(".mp4") || x.ToLower().EndsWith(".mkv"))
                        .Select(PrettyPrintName)
                        .Where(x => x.Contains(showData.ShowName)).ToArray();
                }
                catch (Exception exception)
                {
                    Console.WriteLine("Error: exception encountered when reading download directory ({0}), exiting...", exception.Message);
                    Environment.Exit(1);
                }

                foreach (var existingEpisode in existingEpisodes)
                {
                    Console.WriteLine(existingEpisode);
                }

                var pageContents = GetWebPageContents(url);
                GetEpisodes(pageContents, showData);
                showData.Episodes.ForEach(x => Console.WriteLine(x.Name + ", " + x.TorrentUrl));

                foreach (var episode in showData.Episodes.Where(episode => !existingEpisodes.Contains(episode.Name)))
                {
                    try
                    {
                        downloadProcessStartInfo.Arguments = string.Format("--seed-time=0 --dir=\"{0}\" {1}", downloadDirectory, episode.TorrentUrl);
                        using (var downloadProcess = Process.Start(downloadProcessStartInfo))
                        {
                            downloadProcess.WaitForExit();
                        }
                    }
                    catch (Win32Exception exception)
                    {
                        Console.WriteLine("Error: error when executing downloader ({0}), exiting...", exception.Message);
                        Environment.Exit(1);
                    }
                }

                break;
            }
        }
    }
}
