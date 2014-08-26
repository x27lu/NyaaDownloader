using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using CommandLine;
using MonoTorrent.Client;
using MonoTorrent.Client.Encryption;
using MonoTorrent.Common;
using MonoTorrent.Dht;
using MonoTorrent.Dht.Listeners;

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
        private const string UsageString =
            "Usage: NyaaDownloader.exe [<OPTIONS>] <SHOW NAME> <SUBBER> <DOWNLOAD DIRECTORY>\n"
            + "Example: NyaaDownloader.exe -r 480 -m 4161231234 \"Sword Art Online II\" \"HorribleSubs\" \"C:\\Downloads\"";
        
        private class Options
        {
            [Option('r', "resolution", Required = false, HelpText = "Resolution")]
            public string Resolution { get; set; }

            [Option('m', "sms-receiver", Required = false, HelpText = "Phone # to receive SMS notifications")]
            public string SmsReceiver { get; set; }

            [HelpOption]
            public string GetUsage()
            {
                return UsageString;
            }
        }

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

        public static string BuildSearchUrl(ShowData showData)
        {
            var url = "http://www.nyaa.se/?term=" +
                      "%5B" + showData.Subber + "%5D+" + showData.ShowName.Replace(' ', '+');

            if (showData.Resolution != string.Empty)
                url += "+" + showData.Resolution;
            
            return url;
        }

        public static void GetOnlineEpisodes(string pageContents, ShowData showData)
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

        public static List<string> GetDownloadedEpisodes(string downloadDirectory, ShowData showData)
        {
            var downloadedEpisodes = new List<string>();
            try
            {
                downloadedEpisodes = Directory.GetFiles(downloadDirectory)
                    .Select(Path.GetFileName)
                    .Where(x => x.ToLower().EndsWith(".mp4") || x.ToLower().EndsWith(".mkv"))
                    .Select(PrettyPrintName)
                    .Where(x => x.Contains(showData.ShowName)).ToList();
            }
            catch (Exception exception)
            {
                Console.WriteLine("Error: exception encountered when reading download directory ({0}), exiting...", exception.Message);
                Environment.Exit(1);
            }

            return downloadedEpisodes;
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

        public static void DownloadTorrent(string url, string downloadDirectory, int maxUploadSpeed)
        {
            var torrentFileName = string.Format("{0}.torrent", url.GetHashCode().ToString(CultureInfo.InvariantCulture));
            
            var torrentManager =
                new TorrentManager(
                    Torrent.Load(new Uri(url), torrentFileName),
                    downloadDirectory,
                    new TorrentSettings());

            // find an unused port
            var listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();

            var engineSettings = new EngineSettings(downloadDirectory, port)
                                     {
                                         PreferEncryption = false,
                                         AllowedEncryption = EncryptionTypes.All,
                                         GlobalMaxUploadSpeed = maxUploadSpeed
                                     };

            var dhtListner = new DhtListener(new IPEndPoint(IPAddress.Any, port));
            var dht = new DhtEngine(dhtListner);
            
            var engine = new ClientEngine(engineSettings);
            engine.Register(torrentManager);
            engine.RegisterDht(dht);
            dhtListner.Start();
            engine.DhtEngine.Start();
            
            engine.StartAll();

            while (true)
            {
                var status = string.Format("{0:0.00}% - {1}\nDL:{2:0.00} kB/s    \nUL:{3:0.00} kB/s    ",
                                           torrentManager.Progress,
                                           torrentManager.Torrent.Name,
                                           torrentManager.Monitor.DownloadSpeed/1024.0,
                                           torrentManager.Monitor.UploadSpeed/1024.0);

                var cursorPosition = new Tuple<int, int>(Console.CursorLeft, Console.CursorTop);
                Console.WriteLine(status);

                if (torrentManager.State == TorrentState.Seeding)
                {
                    Console.WriteLine();
                    break;
                }

                Console.SetCursorPosition(cursorPosition.Item1, cursorPosition.Item2);

                Thread.Sleep(500);
            }

            dhtListner.Stop();
            engine.DhtEngine.Stop();
            engine.StopAll();
            engine.Dispose();
            
            File.Delete(torrentFileName);
        }

        private const bool Debug = false;

        static void Main(string[] args)
        {
            // This prevents the framework from trying to autodetect proxy settings which can make the request
            // extremely slow (up to an additional 30 seconds).
            // http://www.nullskull.com/a/848/webclient-class-gotchas-and-basics.aspx
            WebRequest.DefaultWebProxy = null;

            if (args.Length < 3)
            {
                Console.WriteLine(UsageString);
                Environment.Exit(0);
            }

            var options = new Options();
            var parser = new Parser();

            if (!parser.ParseArguments(new List<string>(args).GetRange(0, args.Length - 3).ToArray(), options))
            {
                Console.WriteLine(UsageString);
                Environment.Exit(0);
            }

            var showName = args[args.Length - 3].Trim();
            var subber = args[args.Length - 2].Trim();
            var downloadDirectory = args[args.Length - 1].Trim();

            if (showName == string.Empty || subber == string.Empty || downloadDirectory == string.Empty)
            {
                Console.WriteLine("Error: none of the arguments can be empty, exiting...");
                Environment.Exit(0);
            }

            if (!Directory.Exists(downloadDirectory))
            {
                Console.WriteLine("Error: the specified directory does not exist, exiting...");
                Environment.Exit(0);
            }

            var showData = new ShowData(
                showName.Trim(),
                subber.Trim(),
                options.Resolution != null ? options.Resolution.Trim() : string.Empty);


            Console.WriteLine("\nWill now monitor and download for...");
            Console.WriteLine("   Show: {0}", showData.ShowName);
            Console.WriteLine("   Subber: {0}", showData.Subber);
            if (showData.Resolution != string.Empty)
                Console.WriteLine("   Resolution: {0}", showData.Resolution);
            
            Console.WriteLine("Press any key to continue");
            Console.ReadKey(true);

            Console.WriteLine("\nLink Start!\n");

            var url = BuildSearchUrl(showData);

            var previousCheckFoundNothingNew = false;
            while (true)
            {
                var downloadedEpisodes = GetDownloadedEpisodes(downloadDirectory, showData);
                if (Debug)
                {
                    Console.WriteLine("Downloaded Episodes:");
                    downloadedEpisodes.ForEach(x => Console.WriteLine(" " + x));
                }

                GetOnlineEpisodes(GetWebPageContents(url), showData);
                if (Debug)
                {
                    Console.WriteLine("Online Episodes:");
                    showData.Episodes.ForEach(x => Console.WriteLine(" " + x.Name + ", " + x.TorrentUrl));
                }

                var episodesToDownload = showData.Episodes.Where(episode => !downloadedEpisodes.Contains(episode.Name)).ToList();
                if (Debug)
                {
                    Console.WriteLine("Episodes to Download:");
                    episodesToDownload.ForEach(x => Console.WriteLine(" " + x.Name + ", " + x.TorrentUrl));
                }
                
                if (episodesToDownload.Count > 0)
                {
                    Console.WriteLine("\nFound {0} episodes to download! Starting...\n", episodesToDownload.Count);
                    previousCheckFoundNothingNew = false;

                    foreach (var episode in episodesToDownload)
                    {
                        DownloadTorrent(episode.TorrentUrl, downloadDirectory, 25);
                        if (!String.IsNullOrEmpty(options.SmsReceiver))
                        {
                            Console.WriteLine("Sending SMS...\n");
                            Process.Start("SMSUtil.exe", "\"" + options.SmsReceiver + "\" \"'" + episode.Name + "' has finished downloading\"");
                        }
                    }
                }
                else
                {
                    if (previousCheckFoundNothingNew)
                        Console.SetCursorPosition(0, Console.CursorTop - 1);

                    Console.WriteLine("Nothing to download at this time, waiting...");
                    previousCheckFoundNothingNew = true;

                    // monitor every minute
                    var waitDuration = DateTime.Now.AddMinutes(1) - DateTime.Now;
                    Thread.Sleep(waitDuration);
                }
            }
        }
    }
}
