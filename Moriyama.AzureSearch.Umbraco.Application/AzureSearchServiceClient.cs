﻿using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Microsoft.Azure.Search;
using Microsoft.Azure.Search.Models;
using Moriyama.AzureSearch.Umbraco.Application.Interfaces;
using Moriyama.AzureSearch.Umbraco.Application.Models;
using Newtonsoft.Json;
using Umbraco.Core.Models;
using Umbraco.Core.Persistence;
using Umbraco.Web;
using Newtonsoft.Json.Linq;
using System.Text.RegularExpressions;
using Moriyama.AzureSearch.Umbraco.Application.Extensions;
using Umbraco.Core;
using Umbraco.Web.Models;
using UmbracoExamine;
using UmbracoExamine.DataServices;
using File = System.IO.File;

namespace Moriyama.AzureSearch.Umbraco.Application
{
    public class AzureSearchIndexClient : BaseAzureSearch, IAzureSearchIndexClient
    {
        private Dictionary<string, IComputedFieldParser> Parsers { get; set; }

        // Number of docs to be processed at a time.
        const int BatchSize = 999;

        public AzureSearchIndexClient(string path) : base(path)
        {
            Parsers = new Dictionary<string, IComputedFieldParser>();
            SetCustomFieldParsers(GetConfiguration());

            _propertyCache = new Dictionary<string, Dictionary<string, PropertyInfo>>();
        }

        private Dictionary<string, Dictionary<string, PropertyInfo>> _propertyCache;
        private Lazy<Field[]> _umbracoFields;

        private string SessionFile(string sessionId, string filename)
        {
            var path = Path.Combine(_path, @"App_Data\MoriyamaAzureSearch");
            return Path.Combine(path, sessionId, filename);
        }

        public event EventHandler<Index> CreatingIndex;

        public string DropCreateIndex()
        {
            var serviceClient = GetClient();
            var indexes = serviceClient.Indexes.List().Indexes;

            foreach (var index in indexes)
                if (index.Name == _config.IndexName)
                    serviceClient.Indexes.Delete(_config.IndexName);

            var customFields = new List<Field>();
            customFields.AddRange(GetStandardUmbracoFields());
            customFields.AddRange(_config.SearchFields.Select(x => x.ToAzureField()));

            var definition = new Index
            {
                Name = _config.IndexName,
                Fields = customFields,
                ScoringProfiles = _config.ScoringProfiles,
                Analyzers = _config.Analyzers
            };

            try
            {
                CreatingIndex?.Invoke(this, definition);
                serviceClient.Indexes.Create(definition);
            }
            catch (Exception ex)
            {
                return ex.Message;
            }

            return "Index created";
        }

        public Index[] GetSearchIndexes()
        {
            var serviceClient = GetClient();
            var indexes = serviceClient.Indexes.List().Indexes;
            return indexes.ToArray();
        }

        private void EnsurePath(string path)
        {
            if (!Directory.Exists(path))
                Directory.CreateDirectory(path);
        }

        private void WriteFile(string path, IEnumerable<int> ids)
        {
            var file = new FileInfo(path);
            file.Directory.Create();

            File.WriteAllText(file.FullName, JsonConvert.SerializeObject(ids));
        }

        private void DeleteFile(string sessionId, string path)
        {
            var file = new FileInfo(SessionFile(sessionId, path));
            file.Delete();
        }

        [Obsolete]
        public AzureSearchReindexStatus ReIndexContent(string sessionId)
        {
            return new AzureSearchReindexStatus
            {
                SessionId = sessionId
            };
        }

        private int[] GetIds(string sessionId, string filename)
        {
            var path = Path.Combine(_path, @"App_Data\MoriyamaAzureSearch\" + sessionId);
            var file = Path.Combine(path, filename);

            var ids = JsonConvert.DeserializeObject<int[]>(System.IO.File.ReadAllText(file));
            return ids;
        }

        private int[] Page(int[] collection, int page)
        {
            return collection.Skip((page - 1) * BatchSize).Take(BatchSize).ToArray();
        }

        private List<int> FetchIds(string type)
        {
            using (var db = new UmbracoDatabase("umbracoDbDSN"))
            {
                return db.Fetch<int>($@"select distinct cmsContent.NodeId
                        from cmsContent, umbracoNode where
                        cmsContent.nodeId = umbracoNode.id and
                        umbracoNode.nodeObjectType = '{type}'");
            }
        }

        public AzureSearchReindexStatus ReIndexContent(string sessionId, int page)
        {
            var file = SessionFile(sessionId, "content.json");
            if (!File.Exists(file))
            {
                var contentIds = FetchIds(global::Umbraco.Core.Constants.ObjectTypes.Document);
                WriteFile(file, contentIds);
            }

            return ReIndex("content.json", sessionId, page);
        }

        public AzureSearchReindexStatus ReIndexMedia(string sessionId, int page)
        {
            var file = SessionFile(sessionId, "media.json");
            if (!File.Exists(file))
            {
                List<int> mediaIds;
                using (var db = new UmbracoDatabase("umbracoDbDSN"))
                {
                    mediaIds = db.Fetch<int>(@"select distinct cmsContent.NodeId
                        from cmsContent, umbracoNode where
                        cmsContent.nodeId = umbracoNode.id and
                        umbracoNode.nodeObjectType = 'B796F64C-1F99-4FFB-B886-4BF4BC011A9C'");
                }

                WriteFile(file, mediaIds);
            }

            return ReIndex("media.json", sessionId, page);
        }

        public AzureSearchReindexStatus ReIndexMember(string sessionId, int page)
        {
            var file = SessionFile(sessionId, "member.json");
            if (!File.Exists(file))
            {
                List<int> memberIds;
                using (var db = new UmbracoDatabase("umbracoDbDSN"))
                {
                    memberIds = db.Fetch<int>(@"select distinct cmsContent.NodeId
                    from cmsContent, umbracoNode where
                    cmsContent.nodeId = umbracoNode.id and
                    umbracoNode.nodeObjectType = '39EB0F98-B348-42A1-8662-E7EB18487560'");
                }

                WriteFile(file, memberIds);
            }

            return ReIndex("member.json", sessionId, page);
        }

        public void ReIndexContent(IContent content)
        {
            var documents = new List<Document>();
            var config = GetConfiguration();

            documents.Add(FromUmbracoContent(content, config.SearchFields));
            IndexContentBatch(documents);
        }

        public void ReIndexContent(IMedia content)
        {
            var documents = new List<Document>();
            var config = GetConfiguration();

            documents.Add(FromUmbracoMedia(content, config.SearchFields));
            IndexContentBatch(documents);
        }

        public void Delete(int id)
        {
            var result = new AzureSearchIndexResult();

            var serviceClient = GetClient();

            var actions = new List<IndexAction>();
            var d = new Document();
            d.Add("Id", id.ToString());

            actions.Add(IndexAction.Delete(d));

            var batch = IndexBatch.New(actions);
            var indexClient = serviceClient.Indexes.GetClient(_config.IndexName);

            try
            {
                indexClient.Documents.Index(batch);
            }
            catch (IndexBatchException e)
            {
                // Sometimes when your Search service is under load, indexing will fail for some of the documents in
                // the batch. Depending on your application, you can take compensating actions like delaying and
                // retrying. For this simple demo, we just log the failed document keys and continue.
                var error =
                     "Failed to index some of the documents: {0}" + String.Join(", ", e.IndexingResults.Where(r => !r.Succeeded).Select(r => r.Key));

                result.Success = false;
                result.Message = error;


            }

            result.Success = true;
        }

        public void ReIndexMember(IMember content)
        {
            var documents = new List<Document>();
            var config = GetConfiguration();

            documents.Add(FromUmbracoMember(content, config.SearchFields));
            IndexContentBatch(documents);
        }

        private int GetQueuedItemCount(int[] ids, int page)
        {
            var queued = ids.Length;
            if (page == 0) return queued;

            queued = ids.Length - (BatchSize * page);

            if (queued < 0)
            {
                queued = 0;
            }

            return queued;
        }

        public AzureSearchReindexStatus ReIndex(string filename, string sessionId, int page)
        {
            var ids = GetIds(sessionId, filename);

            var result = new AzureSearchReindexStatus
            {
                SessionId = sessionId
            };

            if (filename == "content.json")
            {
                result.DocumentsQueued = GetQueuedItemCount(ids, page);
            }
            else if (filename == "media.json")
            {
                result.MediaQueued = GetQueuedItemCount(ids, page);
            }
            else if (filename == "members.json")
            {
                result.MembersQueued = GetQueuedItemCount(ids, page);
            }

            if (page > 0)
            {
                var idsToProcess = Page(ids, page);

                if (!idsToProcess.Any())
                {
                    result.Finished = true;
                    return result;
                }

                var documents = new List<Document>();
                var config = GetConfiguration();

                if (filename == "content.json")
                {
                    var contents = UmbracoContext.Current.Application.Services.ContentService.GetByIds(idsToProcess);
                    foreach (var content in contents)
                        if (content != null)
                            documents.Add(FromUmbracoContent(content, config.SearchFields));
                }
                else if (filename == "media.json")
                {
                    var contents = UmbracoContext.Current.Application.Services.MediaService.GetByIds(idsToProcess);

                    foreach (var content in contents)
                        if (content != null)
                            documents.Add(FromUmbracoMedia(content, config.SearchFields));
                }
                else if (filename == "members.json")
                {
                    var contents = new List<IMember>();

                    foreach (var id in idsToProcess)
                        contents.Add(UmbracoContext.Current.Application.Services.MemberService.GetById(id));

                    foreach (var content in contents)
                        if (content != null)
                            documents.Add(FromUmbracoMember(content, config.SearchFields));
                }

                var indexStatus = IndexContentBatch(documents);

                if (!indexStatus.Success)
                    result.Error = true;

                var totalPages = (int) Math.Ceiling((double) (ids.Length / BatchSize)) + 1;
                if (page == totalPages)
                {
                    DeleteFile(sessionId, filename);
                    result.Message = "Done";
                }
                else
                {
                    result.Message = $"Sent {filename.Replace(".json", "")} page {page + 1} of {totalPages} for indexing. {indexStatus.Message}";
                }
            }

            return result;
        }

        private AzureSearchIndexResult IndexContentBatch(IEnumerable<Document> contents)
        {
            var serviceClient = GetClient();
            return serviceClient.IndexContentBatch(_config.IndexName, contents);
        }

        private Document FromUmbracoMember(IMember member, SearchField[] searchFields)
        {
            var result = GetDocumentToIndex((ContentBase)member, searchFields);

            if (member != null)
            {
                result["MemberEmail"] = member.Email;
                result["ContentTypeAlias"] = member.ContentType.Alias;
            }

            result["Icon"] = member.ContentType.Icon;

            return result;
        }

        private Document FromUmbracoMedia(IMedia content, SearchField[] searchFields)
        {
            var result = GetDocumentToIndex((ContentBase)content, searchFields);

            var url = string.Empty;

            if (!content.ContentType.Alias.Equals("Folder"))
            {
                if (content.HasProperty("umbracoFile"))
                {
                    var prop = content.Properties?["umbracoFile"];
                    if (prop != null)
                    {
                        switch (prop.PropertyType.PropertyEditorAlias)
                        {
                            case Constants.PropertyEditors.UploadFieldAlias:
                                url = prop.Value?.ToString();
                                break;

                            case Constants.PropertyEditors.ImageCropperAlias:
                                //get the url from the json format

                                var json = prop.Value as JObject;
                                if (json == null)
                                {
                                    url = prop.Value.ToString();
                                }
                                else
                                {
                                    url = json.ToObject<ImageCropDataSet>(new JsonSerializer {Culture = CultureInfo.InvariantCulture, FloatParseHandling = FloatParseHandling.Decimal}).Src;
                                }
                                break;
                        }
                    }
                }
            }


            result["Url"] = url;
            result["ContentTypeAlias"] = content.ContentType.Alias;
            result["Icon"] = content.ContentType.Icon;

            return result;
        }

        private Document FromUmbracoContent(IContent content, SearchField[] searchFields)
        {
            var result = GetDocumentToIndex((ContentBase)content, searchFields);

            result["Published"] = content.Published;
            result["WriterId"] = content.WriterId;
            result["WriterName"] = content.GetWriterProfile(UmbracoContext.Current.Application.Services.UserService).Name;
            result["ContentTypeAlias"] = content.ContentType.Alias;

            if (content.Published)
            {
                var helper = new UmbracoHelper(UmbracoContext.Current);
                var publishedContent = helper.TypedContent(content.Id);

                if (publishedContent != null)
                {
                    result["Url"] =  publishedContent.Url;
                }
            }

            // SLOW:
            //var isProtected = UmbracoContext.Current.Application.Services.PublicAccessService.IsProtected(content.Path);
            //result.Add("IsProtected", content.ContentType.Alias);

            if (content.Template != null)
                result["Template"] = content.Template.Alias;

            result["Icon"] = content.ContentType.Icon;

            return result;
        }



        private Document GetDocumentToIndex(IContentBase content, SearchField[] searchFields)
        {
            try
            {

                var c = new Document();

                var type = content.GetType();

                foreach (var field in GetStandardUmbracoFields())
                {
                    try
                    {
                        object propertyValue = null;

                        // handle special case properties
                        switch (field.Name)
                        {
                            case "SearchablePath":
                                propertyValue = content.Path.TrimStart('-');
                                break;

                            case "Path":
                                propertyValue = content.Path.Split(',');
                                break;

                            case "CreatorName":
                                propertyValue = content.GetCreatorProfile(UmbracoContext.Current.Application.Services.UserService).Name;
                                break;

                            case "ParentID":
                                propertyValue = content.ParentId;
                                break;

                            default:
                                // try get model property
                                PropertyInfo modelProperty;
                                if (!_propertyCache.ContainsKey(type.Name))
                                {
                                    _propertyCache[type.Name] = new Dictionary<string, PropertyInfo>();
                                }

                                var cache = _propertyCache[type.Name];
                                
                                if (cache.ContainsKey(field.Name))
                                {
                                    modelProperty = cache[field.Name];
                                }
                                else
                                {
                                    modelProperty = type.GetProperty(field.Name);
                                    cache[field.Name] = modelProperty;
                                }

                                if (modelProperty != null)
                                {
                                    propertyValue = modelProperty.GetValue(content);
                                }
                                else
                                {
                                    // try get umbraco property
                                    if (content.HasProperty(field.Name))
                                    {
                                        propertyValue = content.GetValue(field.Name);
                                    }
                                }
                                break;
                        }

                        // handle datatypes
                        switch (field.Type.ToString())
                        {
                            case "Edm.String":
                                propertyValue = propertyValue?.ToString();
                                break;

                            case "Edm.Boolean":
                                bool.TryParse((propertyValue ?? "False").ToString(), out var val);
                                propertyValue = val;
                                break;
                        }

                        if (propertyValue?.ToString().IsNullOrWhiteSpace() == false)
                        {
                            c[field.Name] = propertyValue;
                        }
                    }
                    catch (Exception ex)
                    {

                        throw;
                    }
                }

                switch (type.Name)
                {
                    case "Media":
                        c["IsMedia"] = true;
                        break;

                    case "Content":
                        c["IsContent"] = true;
                        break;

                    case "Member":
                        c["IsMember"] = true;
                        break;
                }

                bool cancelIndex = AzureSearch.FireContentIndexing(
                    new AzureSearchEventArgs()
                    {
                        Item = content,
                        Entry = c
                    });

                if (cancelIndex)
                {
                    // cancel was set in an event, so we don't index this item. 
                    return null;
                }

                var umbracoFields = searchFields.Where(x => !x.IsComputedField()).ToArray();
                var computedFields = searchFields.Where(x => x.IsComputedField()).ToArray();

                c = FromUmbracoContentBase(c, content, umbracoFields);
                c = FromComputedFields(c, content, computedFields);

                // todo: content isn't actually indexed at this point, consider moving the event to the callback from azure after sending to index
                AzureSearch.FireContentIndexed(
                    new AzureSearchEventArgs()
                    {
                        Item = content,
                        Entry = c
                    });

                return c;
            }
            catch (Exception ex)
            {

                throw;
            }
        }

        private Document FromUmbracoContentBase(Document c, IContentBase content, SearchField[] searchFields)
        {
            foreach (var field in searchFields)
            {
                if (!content.HasProperty(field.Name) || content.Properties[field.Name].Value == null)
                {
                    if (field.Type == "collection")
                        c.Add(field.Name, new List<string>());

                    if (field.Type == "string")
                        c.Add(field.Name, string.Empty);

                    if (field.Type == "int")
                        c.Add(field.Name, 0);

                    if (field.Type == "bool")
                        c.Add(field.Name, false);

                }
                else
                {
                    var value = content.Properties[field.Name].Value;

                    if (field.Type == "collection")
                    {
                        if (!string.IsNullOrEmpty(value.ToString()))
                            c.Add(field.Name, value.ToString().Split(','));
                    }
                    else
                    {
                        if (field.IsGridJson)
                        {
                            // #filth #sorrymarc
                            JObject jObject = JObject.Parse(value.ToString());
                            var tokens = jObject.SelectTokens("..value");

                            try
                            {
                                var values = tokens.Where(x => x != null).Select(x => (x as JValue).Value);
                                value = string.Join(" ", values);
                                value = Regex.Replace(value.ToString(), "<.*?>", String.Empty);
                                value = value.ToString().Replace(Environment.NewLine, " ");
                                value = value.ToString().Replace(@"\n", " ");
                            }
                            catch (Exception ex)
                            {
                                value = string.Empty;
                            }
                        }

                        c.Add(field.Name, value);
                    }
                }
            }

            return c;
        }

        private Document FromComputedFields(Document document, IContentBase content, SearchField[] customFields)
        {
            if (customFields != null)
            {
                foreach (var customField in customFields)
                {
                    var parser = Parsers.Single(x => x.Key == customField.ParserType).Value;
                    document.Add(customField.Name, parser.GetValue(content));
                }
            }

            return document;
        }

        private void SetCustomFieldParsers(AzureSearchConfig azureSearchConfig)
        {
            if (azureSearchConfig.SearchFields != null)
            {
                var types = azureSearchConfig.SearchFields.Where(x => x.IsComputedField()).Select(x => x.ParserType).Distinct().ToArray();

                foreach (var t in types)
                {
                    var parser = Activator.CreateInstance(Type.GetType(t));

                    if (!(parser is IComputedFieldParser))
                    {
                        throw new Exception(string.Format("Type {0} does not implement {1}", t, typeof(IComputedFieldParser).Name));
                    }

                    Parsers.Add(t, (IComputedFieldParser)parser);
                }
            }
        }

        public Field[] GetStandardUmbracoFields()
        {
            var cachedFields = ApplicationContext.Current.ApplicationCache.RuntimeCache.GetCacheItem("AzureSearch_UmbracoFields", () => 
            {
                    var fields = new List<Field>
                    {
                         // Key field has to be a string....
                         new Field("Id", DataType.String) { IsKey = true, IsFilterable = true, IsSortable = true },
                         new Field("Name", DataType.String) { IsFilterable = true, IsSortable = true, IsSearchable = true, IsRetrievable = true},
                         new Field("Key", DataType.String) { IsSearchable = true, IsRetrievable = true},

                         new Field("Url", DataType.String) { IsSearchable = true, IsRetrievable = true},
                         new Field("MemberEmail", DataType.String) { IsSearchable = true },

                         new Field("IsContent", DataType.Boolean) { IsFilterable = true, IsFacetable = true },
                         new Field("IsMedia", DataType.Boolean) { IsFilterable = true, IsFacetable = true },
                         new Field("IsMember", DataType.Boolean) { IsFilterable = true, IsFacetable = true },

                         new Field("Published", DataType.Boolean) { IsFilterable = true, IsFacetable = true },
                         new Field("Trashed", DataType.Boolean) { IsFilterable = true, IsFacetable = true },

                         new Field("SearchablePath", DataType.String) { IsSearchable = true, IsFilterable = true},
                         new Field("Path", DataType.Collection(DataType.String)) { IsSearchable = true, IsFilterable = true },
                         new Field("Template", DataType.String) { IsSearchable = true, IsFacetable = true },
                         new Field("Icon", DataType.String) { IsSearchable = true, IsFacetable = true },

                         new Field("ContentTypeAlias", DataType.String) { IsSearchable = true, IsFacetable = true, IsFilterable = true },

                         new Field("UpdateDate", DataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },
                         new Field("CreateDate", DataType.DateTimeOffset) { IsFilterable = true, IsSortable = true },

                         new Field("ContentTypeId", DataType.Int32) { IsFilterable = true },
                         new Field("ParentID", DataType.String) { IsFilterable = true, IsSearchable = true},
                         new Field("Level", DataType.Int32) { IsSortable = true, IsFacetable = true },
                         new Field("SortOrder", DataType.Int32) { IsSortable = true },

                         new Field("WriterId", DataType.Int32) { IsSortable = true, IsFacetable = true },
                         new Field("CreatorId", DataType.Int32) { IsSortable = true, IsFacetable = true },
                         new Field("WriterName", DataType.String) { IsSortable = true, IsFacetable = true },
                         new Field("CreatorName", DataType.String) { IsSortable = true, IsFacetable = true }
                    };
            
                    var examineContentService =  new UmbracoContentService(ApplicationContext.Current);

                    var existingNames = fields.Select(f => f.Name);

                    // get system properties which haven't already been defined above
                    foreach (var name in examineContentService.GetAllSystemPropertyNames().Where(fn => !existingNames.InvariantContains(fn)))
                    {
                        fields.Add(new Field
                        {
                            Name = name,
                            Type = DataType.String,
                            IsFilterable = true, 
                            IsSearchable = true,
                            // Treats the entire content of a field as a single token
                            Analyzer = AnalyzerName.Keyword
                        });
                    }
            
                    // get 'system' fields like umbracoWidth, umbracoBytes, umbracoNaviHide etc
                    var umbracoFields = examineContentService.GetAllUserPropertyNames().Where(f => f.StartsWith("umbraco"));
                    foreach (var name in umbracoFields)
                    {
                        fields.Add(new Field
                        {
                            Name = name,
                            Type = DataType.String,
                            IsFilterable = true, 
                            IsSearchable = true,
                            // Treats the entire content of a field as a single token
                            Analyzer = AnalyzerName.Keyword
                        });
                    }

                    // sort, but ensure key is always first
                    var keyField = fields.FirstOrDefault(f => f.IsKey);
                    var sorted = fields.Except(new [] { keyField }).OrderBy(f => f.Name).ToList();
                    sorted.Insert(0, keyField);

                    return sorted.ToArray();
            }, TimeSpan.FromMinutes(5)) as Field[];

            return cachedFields;
        }
    }
}
