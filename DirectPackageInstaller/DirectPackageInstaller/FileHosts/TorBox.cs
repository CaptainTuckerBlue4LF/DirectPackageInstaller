using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;

namespace DirectPackageInstaller.FileHosts
{
    // TorBox prepares supported hoster links as web downloads before returning a CDN URL.
    class TorBox : FileHostBase
    {
        private const string ApiBase = "https://api.torbox.app/v1/api/webdl";
        private static readonly Dictionary<string, string> GeneratedLinks = new Dictionary<string, string>();

        public override string HostName => "TorBox";
        public override bool Limited => false;

        public override bool IsValidUrl(string URL)
        {
            return App.Config.UseTorBox
                && !string.IsNullOrWhiteSpace(App.Config.TorBoxApiKey)
                && !App.Config.TorBoxApiKey.Equals("null", StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(URL, UriKind.Absolute, out var uri)
                && !uri.Host.Contains("torbox.app", StringComparison.OrdinalIgnoreCase);
        }

        public override DownloadInfo GetDownloadInfo(string URL)
        {
            if (GeneratedLinks.TryGetValue(URL, out var cached))
                return new DownloadInfo { Url = cached };

            using var client = CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, ApiBase + "/createwebdownload")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["link"] = URL })
            };

            var createResponse = client.SendAsync(request).GetAwaiter().GetResult();
            var createJson = ReadResponse(createResponse);
            var webDownloadId = GetId(GetData(createJson), "id", "webdownload_id", "web_id");

            // TorBox may need time to retrieve an uncached file. Poll only this new item.
            for (var attempt = 0; attempt < 150; attempt++)
            {
                Thread.Sleep(2000);
                var statusResponse = client.GetAsync(ApiBase + "/mylist?id=" + webDownloadId).GetAwaiter().GetResult();
                var statusJson = ReadResponse(statusResponse);
                var item = GetFirstItem(GetData(statusJson));

                if (IsReady(item))
                {
                    var fileId = GetFileId(item);
                    var requestUrl = ApiBase + "/requestdl?token=" + Uri.EscapeDataString(App.Config.TorBoxApiKey)
                        + "&web_id=" + webDownloadId
                        + "&file_id=" + fileId
                        + "&redirect=true&append_name=true";
                    GeneratedLinks[URL] = requestUrl;
                    return new DownloadInfo { Url = requestUrl };
                }
            }

            throw new TimeoutException("TorBox did not finish preparing the download in time.");
        }

        private static HttpClient CreateClient()
        {
            var client = new HttpClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", App.Config.TorBoxApiKey);
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            return client;
        }

        private static JsonElement ReadResponse(HttpResponseMessage response)
        {
            var content = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(content);
            var root = document.RootElement.Clone();

            if (!response.IsSuccessStatusCode || !root.TryGetProperty("success", out var success) || !success.GetBoolean())
            {
                var detail = root.TryGetProperty("detail", out var value) ? value.GetString() : content;
                throw new InvalidOperationException("TorBox API error: " + detail);
            }

            return root;
        }

        private static JsonElement GetData(JsonElement root) =>
            root.TryGetProperty("data", out var data) ? data : root;

        private static JsonElement GetFirstItem(JsonElement data) =>
            data.ValueKind == JsonValueKind.Array && data.GetArrayLength() > 0 ? data[0] : data;

        private static int GetId(JsonElement item, params string[] names)
        {
            var value = GetOptionalId(item, names);
            if (value == 0)
                throw new InvalidOperationException("TorBox did not return a web download ID.");
            return value;
        }

        private static int GetOptionalId(JsonElement item, params string[] names)
        {
            foreach (var name in names)
            {
                if (item.TryGetProperty(name, out var value) && value.TryGetInt32(out var id))
                    return id;
            }

            if (item.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0)
                return GetOptionalId(files[0], names);

            return 0;
        }

        private static int GetFileId(JsonElement item)
        {
            if (item.TryGetProperty("files", out var files) && files.ValueKind == JsonValueKind.Array && files.GetArrayLength() > 0)
                return GetOptionalId(files[0], "file_id", "id");

            // The API defaults file_id to 0, which selects the first file.
            return 0;
        }

        private static bool IsReady(JsonElement item)
        {
            if (item.TryGetProperty("download_finished", out var finished) && finished.ValueKind == JsonValueKind.True)
                return true;

            if (item.TryGetProperty("status", out var status))
            {
                var value = status.GetString();
                return string.Equals(value, "cached", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "finished", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(value, "completed", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }
    }
}
