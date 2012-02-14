using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.ServiceModel.Syndication;
using System.Text;
using System.Web;
using System.Xml;
using JabbR.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Serialization;

namespace JabbR.Handlers
{
    public class MessagesHandler : IHttpHandler
    {
        const string FilenameDateFormat = "yyyy-MM-dd.HHmmsszz";

        IJabbrRepository _repository;

        public MessagesHandler(IJabbrRepository repository)
        {
            _repository = repository;
        }

        public bool IsReusable
        {
            get { return false; }
        }

        public void ProcessRequest(HttpContext context)
        {
            var request = context.Request;
            var response = context.Response;
            var routeData = request.RequestContext.RouteData.Values;

            var roomName = (string)routeData["room"];
            var formatName = (string)routeData["format"];
            var range = request["range"];

            if (String.IsNullOrWhiteSpace(range))
            {
                range = "last-day";
            }

            var end = DateTime.Now;
            DateTime start;

            switch (range)
            {
                case "last-hour":
                    start = end.AddHours(-1);
                    break;
                case "last-day":
                    start = end.AddDays(-1);
                    break;
                case "last-week":
                    start = end.AddDays(-7);
                    break;
                case "last-month":
                    start = end.AddDays(-30);
                    break;
                case "all":
                    start = DateTime.MinValue;
                    break;
                default:
                    WriteBadRequest(response, "range value not recognized");
                    return;
            }

            ChatRoom room = null;

            try
            {
                room = _repository.VerifyRoom(roomName, mustBeOpen: false);
            }
            catch (Exception ex)
            {
                WriteNotFound(response, ex.Message);
                return;
            }

            if (room.Private)
            {
                // TODO: Allow viewing messages using auth token
                WriteNotFound(response, String.Format("Unable to locate room {0}.", room.Name));
                return;
            }

            var messages = _repository.GetMessagesByRoom(roomName)
                    .Where(msg => msg.When <= end && msg.When >= start);

            bool downloadFile = false;
            Boolean.TryParse(request["download"], out downloadFile);

            if (downloadFile)
            {
                var downloadFilename = roomName + ".";

                if (start != DateTime.MinValue)
                {
                    downloadFilename += start.ToString(FilenameDateFormat, CultureInfo.InvariantCulture) + ".";
                }

                downloadFilename += end.ToString(FilenameDateFormat, CultureInfo.InvariantCulture) + "." + formatName;

                response.Headers["Content-Disposition"] = "attachment; filename=\"" + downloadFilename + "\"";
            }

            switch (formatName)
            {
                case "json":
                    WriteJsonResponse(response, messages);
                    break;
                case "rss":
                    WriteRssResponse(response, room.Name, messages);
                    break;
                default:
                    //TODO: if the format isn't json, do you really want to send a json error message here?
                    WriteBadRequest(response, "format not supported.");
                    return;
            }
        }

        private void WriteBadRequest(HttpResponse response, string message)
        {
            WriteError(response, 400, "Bad request", message);
        }

        private void WriteNotFound(HttpResponse response, string message)
        {
            WriteError(response, 404, "Not found", message);
        }

        private void WriteError(HttpResponse response, int statusCode, string description, string message)
        {
            response.TrySkipIisCustomErrors = true;
            response.StatusCode = statusCode;
            response.StatusDescription = description;
            response.Write(SerializeJson(new ClientError { Message = message }));
        }

        private void WriteRssResponse(HttpResponse response, string roomName, IEnumerable<ChatMessage> messages)
        {
            messages = messages.OrderByDescending(msg => msg.When);

            var lastUpdated = messages.Select(msg => msg.When).FirstOrDefault();

            var items = messages.Select(msg => 
            {
                var item = new SyndicationItem
                {
                    PublishDate = msg.When,
                    LastUpdatedTime = msg.When,
                    Id = msg.Id,
                    Content = new TextSyndicationContent(msg.Content, TextSyndicationContentKind.Html)
                };

                item.ElementExtensions.Add("creator", "http://purl.org/dc/elements/1.1/", msg.User.Name);

                return item;
            });

            var feed = new SyndicationFeed(string.Format("JabbR - {0}", roomName), string.Empty, new Uri(string.Format("http://jabbr.net/#/rooms/{0}", roomName)), roomName, lastUpdated, items);

            response.ContentType = "application/rss+xml";
            response.ContentEncoding = Encoding.UTF8;

            var formatter = new Rss20FeedFormatter(feed);

            var settings = new XmlWriterSettings 
            {
                Encoding = Encoding.UTF8
            };

            using (var xml = XmlWriter.Create(response.OutputStream, settings))
            {
                formatter.WriteTo(xml);
            }
        }

        private string SerializeJson(object value)
        {
            var resolver = new CamelCasePropertyNamesContractResolver();
            var settings = new JsonSerializerSettings
            {
                ContractResolver = resolver
            };

            settings.Converters.Add(new IsoDateTimeConverter());

            return JsonConvert.SerializeObject(value, Newtonsoft.Json.Formatting.Indented, settings);
        }

        private void WriteJsonResponse(HttpResponse response, IEnumerable<ChatMessage> messages)
        {
            var jsonMessages = messages.Select(msg => new {
                Content = msg.Content,
                Username = msg.User.Name,
                When = msg.When
            });

            var json = SerializeJson(jsonMessages);

            var data = Encoding.UTF8.GetBytes(json);

            response.ContentType = "application/json";
            response.ContentEncoding = Encoding.UTF8;

            response.BinaryWrite(data);
        }

        private class ClientError
        {
            public string Message { get; set; }
        }
    }
}