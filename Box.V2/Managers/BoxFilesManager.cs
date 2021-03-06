﻿using Box.V2.Auth;
using Box.V2.Config;
using Box.V2.Converter;
using Box.V2.Extensions;
using Box.V2.Models;
using Box.V2.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;

namespace Box.V2.Managers
{
    /// <summary>
    /// File objects represent that metadata about individual files in Box, with attributes describing who created the file, 
    /// when it was last modified, and other information. 
    /// </summary>
    public class BoxFilesManager : BoxResourceManager
    {
        public BoxFilesManager(IBoxConfig config, IBoxService service, IBoxConverter converter, IAuthRepository auth, string asUser = null, bool? suppressNotifications = null)
            : base(config, service, converter, auth, asUser, suppressNotifications) { }

        /// <summary>
        /// Retrieves metadata about file.
        /// </summary>
        /// <param name="id">Id of file information to retrieve.</param>
        /// <param name="fields">Attribute(s) to include in the response</param>
        /// <returns>A full file object is returned if the ID is valid and if the user has access to the file.</returns>
        public async Task<BoxFile> GetInformationAsync(string id, List<string> fields = null)
        {
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, id)
                .Param(ParamFields, fields);

            IBoxResponse<BoxFile> response = await ToResponseAsync<BoxFile>(request).ConfigureAwait(false);

            return response.ResponseObject;
        }

        /// <summary>
        /// Returns the stream of the requested file
        /// </summary>
        /// <param name="id">Id of the file to download</param>
        /// <param name="versionId">The ID specific version of this file to download.</param>
        /// <param name="timeout">Optional timeout for response</param>
        /// <returns>MemoryStream of the requested file</returns>
        public async Task<Stream> DownloadStreamAsync(string id, string versionId = null, TimeSpan? timeout = null)
        {
            var uri = await GetDownloadUriAsync(id, versionId);
            BoxRequest request = new BoxRequest(uri)
                {
                    Timeout=timeout
                };
            IBoxResponse<Stream> response = await ToResponseAsync<Stream>(request).ConfigureAwait(false);
            return response.ResponseObject;
           
        }

        /// <summary>
        /// Retrieves the actual data of the file. An optional version parameter can be set to download a previous version of the file.
        /// </summary>
        /// <param name="id">Id of the file</param>
        /// <param name="versionId">The ID specific version of this file to download.</param>
        /// <param name="range">The range value in bytes</param>
        /// <param name="boxAPI">The shared link for this item.</param>
        /// <returns>If the file is available to be downloaded, the response is a URL at dl.boxcloud.com.</returns>
        public async Task<Uri> GetDownloadUriAsync(string id, string versionId = null,
            BoxRangeRequest range = null, string boxAPI = null)
        {
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, string.Format(Constants.ContentPathString, id)) { FollowRedirect = false }
                .Param("version", versionId);
            if (range != null)
            {
                request.Param("bytes", string.Format("{0}-{1}", range.StartRange, range.EndRange));
            }
            if (!string.IsNullOrEmpty(boxAPI))
            {
                request.Param("shared_link", boxAPI);
            }
            IBoxResponse<BoxFile> response = await ToResponseAsync<BoxFile>(request).ConfigureAwait(false);
            var locationUri = response.Headers.Location;

            return locationUri;
        }

        /// <summary>
        /// The Pre-flight check API will verify that a file will be accepted by Box before you send all the bytes over the wire. It can be used for both first-time uploads, and uploading new versions of an existing file.
        /// </summary>
        /// <remarks>
        /// Preflight checks verify all permissions as if the file was actually uploaded including:
        /// Folder upload permission
        /// File name collisions
        /// file size caps
        /// folder and file name restrictions*
        /// folder and account storage quota
        /// </remarks>
        /// <param name="preflightCheckRequest">Fill required inputs: Name - The name of the file to be uploaded, Parent.Id - The ID of the parent folder.,
        /// Size - The size of the file in bytes. Specify 0 for unknown file-sizes
        /// </param>
        /// <returns>If true is returned if the upload would be successful. An error is thrown when any of the preflight conditions are not met.</returns>
        public async Task<bool> PreflightCheck(BoxPreflightCheckRequest preflightCheckRequest)
        {
            preflightCheckRequest.ThrowIfNull("preflightCheckRequest")
                .Name.ThrowIfNullOrWhiteSpace("preflightCheckRequest.Name");
           
            preflightCheckRequest.Parent.ThrowIfNull("preflightCheckRequest.Parent")
                .Id.ThrowIfNullOrWhiteSpace("preflightCheckRequest.Parent.Id");

            BoxRequest request = new BoxRequest(_config.FilesPreflightCheckUri)
                .Method(RequestMethod.Options);

            request.Payload = _converter.Serialize(preflightCheckRequest);
            request.ContentType = Constants.RequestParameters.ContentTypeJson;

            IBoxResponse<BoxPreflightCheck> response = await ToResponseAsync<BoxPreflightCheck>(request).ConfigureAwait(false);

            return response.Status == ResponseStatus.Success;
        }

        /// <summary>
        /// Uploads a provided file to the target parent folder 
        /// If the file already exists, an error will be thrown.
        /// A proper timeout should be provided for large uploads
        /// </summary>
        /// <param name="fileRequest">Upload file data.
        /// Mandatory fields:
        /// fileRequest.Name - name of the file
        /// fileRequest.Parent.Id - Designates folder_id of parent object. Use 0 for the root folder.
        /// Optional fields:
        /// fileRequest.ContentCreatedAt - time when the file was created.
        /// fileRequest.ContentModifiedAt - time hwne the contents of a file were last modified.
        /// </param>
        /// <param name="stream">Stream of uploading file.</param>
        /// <param name="fields">Fields which shall be returned in result</param>
        /// <param name="timeout">Optional timeout for response</param>
        /// <param name="contentMD5">The SHA1 hash of the file</param>
        /// <param name="setStreamPositionToZero">Set position for input stream to 0</param>
        /// <returns>A full file object is returned inside of a collection if the ID is valid and if the update is successful.</returns>
        public async Task<BoxFile> UploadAsync(BoxFileRequest fileRequest, Stream stream, List<string> fields = null,
                                                TimeSpan? timeout = null, byte[] contentMD5 = null,
                                                bool setStreamPositionToZero = true)
        {
            stream.ThrowIfNull("stream");
            fileRequest.ThrowIfNull("fileRequest")
                .Name.ThrowIfNullOrWhiteSpace("filedRequest.Name");
            fileRequest.Parent.ThrowIfNull("fileRequest.Parent")
                .Id.ThrowIfNullOrWhiteSpace("fileRequest.Parent.Id");

            if (setStreamPositionToZero)
            {
                stream.Position = 0;
            }

            BoxMultiPartRequest request = new BoxMultiPartRequest(_config.FilesUploadEndpointUri) { Timeout = timeout }
                .Method(RequestMethod.Post)
                .Param(ParamFields, fields)
                .FormPart(new BoxStringFormPart()
                {
                    Name = "attributes",
                    Value = _converter.Serialize(fileRequest)
                })
                .FormPart(new BoxFileFormPart()
                {
                    Name = "file",
                    Value = stream,
                    FileName = fileRequest.Name
                });

            if (contentMD5 != null)
            {
                request.Header(Constants.RequestParameters.ContentMD5, HexStringFromBytes(contentMD5));
            }

            IBoxResponse<BoxCollection<BoxFile>> response = await ToResponseAsync<BoxCollection<BoxFile>>(request).ConfigureAwait(false);

            // We can only upload one file at a time, so return the first entry
            return response.ResponseObject.Entries.FirstOrDefault();
        }

        /// <summary>
        /// This method is used to upload a new version of an existing file in a user’s account. Similar to regular file uploads, 
        /// these are performed as multipart form uploads An optional If-Match header can be included to ensure that client only 
        /// overwrites the file if it knows about the latest version. The filename on Box will remain the same as the previous version.
        /// A proper timeout should be provided for large uploads
        /// </summary>
        /// <param name="fileName">Name of the file</param>
        /// <param name="fileId">Id of the updated file</param>
        /// <param name="stream">Stream of uploading file</param>
        /// <param name="etag">Etag field of the file object</param>
        /// <param name="fields">Fields which shall be returned in result</param>
        /// <param name="timeout">Optional timeout for response</param>
        /// <param name="contentMD5">The SHA1 hash of the file</param>
        /// <param name="setStreamPositionToZero">Set position for input stream to 0</param>
        /// <returns>A full file object is returned</returns>
        public async Task<BoxFile> UploadNewVersionAsync(string fileName, string fileId, Stream stream,
                                                         string etag = null, List<string> fields = null,
                                                         TimeSpan? timeout = null, byte[] contentMD5 = null,
                                                         bool setStreamPositionToZero = true
                                                         )
        {
            stream.ThrowIfNull("stream");
            fileName.ThrowIfNullOrWhiteSpace("fileName");

            if (setStreamPositionToZero)
                stream.Position = 0;
            
            Uri uploadUri = new Uri(string.Format(Constants.FilesNewVersionEndpointString, fileId));

            BoxMultiPartRequest request = new BoxMultiPartRequest(uploadUri) { Timeout = timeout }
                .Header("If-Match", etag)
                .Param(ParamFields, fields)
                .FormPart(new BoxFileFormPart()
                {
                    Name = "filename",
                    Value = stream,
                    FileName = fileName
                });

            if (contentMD5 != null)
                request.Header(Constants.RequestParameters.ContentMD5, HexStringFromBytes(contentMD5));

            IBoxResponse<BoxCollection<BoxFile>> response = await ToResponseAsync<BoxCollection<BoxFile>>(request).ConfigureAwait(false);

            // We can only upload one file at a time, so return the first entry
            return response.ResponseObject.Entries.FirstOrDefault();
        }

        private string HexStringFromBytes(byte[] bytes)
        {
            var sb = new StringBuilder();
            foreach (byte b in bytes)
            {
                var hex = b.ToString("x2");
                sb.Append(hex);
            }
            return sb.ToString();
        }

        /// <summary>
        /// If there are previous versions of this file, this method can be used to retrieve metadata about the older versions.
        /// <remarks>Versions are only tracked for Box users with premium accounts.</remarks>
        /// </summary>
        /// <param name="id"></param>
        /// <returns>A collection of versions other than the main version of the file. If a file has no other versions, an empty collection will be returned.
        /// Note that if a file has a total of three versions, only the first two version will be returned.</returns>
        public async Task<BoxCollection<BoxFileVersion>> ViewVersionsAsync(string id, List<string> fields = null)
        {
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, string.Format(Constants.VersionsPathString, id))
                .Param(ParamFields, fields);

            IBoxResponse<BoxCollection<BoxFileVersion>> response = await ToResponseAsync<BoxCollection<BoxFileVersion>>(request).ConfigureAwait(false);

            return response.ResponseObject;
        }

        /// <summary>
        /// Used to update individual or multiple fields in the file object, including renaming the file, changing it’s description, 
        /// and creating a shared link for the file. To move a file, change the ID of its parent folder. An optional etag
        /// can be included to ensure that client only updates the file if it knows about the latest version.
        /// </summary>
        /// <param name="id">Id of the file.</param>
        /// <param name="fileRequest">Specifies file update data which contains:
        /// fileRequest.Name - The new name for the file.
        /// fileRequest.Description - The new description for the file.
        /// fileRequest.Parent.Id -  The ID of the parent folder.
        /// filerequest.SharedLink.Access - The level of access required for this shared link. Can be open, company, collaborators.
        /// filerequest.SharedLink.UnsharedAt -  The day that this link should be disabled at. Timestamps are rounded off to the given day.
        /// filerequest.SharedLink.Permissions.Download - Whether this link allows downloads.
        /// filerequest.SharedLink.Permissions.Preview - Whether this link allows previews.
        /// fileRequest.Tags - All tags attached to this file. To add/remove a tag to/from a file, you can first get the file’s current tags (be sure to specify ?fields=tags, since the tags field is not returned by default); then modify the list as required; and finally, set the file’s entire list of tags.
        /// </param>
        /// <param name="etag">The etag of the file. This is in the ‘etag’ field of the file object.</param>
        /// <param name="fields">Fields which shall be returned in result.</param>
        /// <returns>A full file object is returned if the ID is valid and if the user has access to the file.</returns>
        public async Task<BoxFile> UpdateInformationAsync(string id, BoxFileRequest fileRequest, string etag = null, List<string> fields = null)
        {
            id.ThrowIfNullOrWhiteSpace("id");
            fileRequest.ThrowIfNull("fileRequest");            

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, id)
                .Method(RequestMethod.Put)
                .Header("If-Match", etag)
                .Param(ParamFields, fields)
                .Payload(_converter.Serialize(fileRequest));

            IBoxResponse<BoxFile> response = await ToResponseAsync<BoxFile>(request).ConfigureAwait(false);

            return response.ResponseObject;
        }

        /// <summary>
        /// Discards a file to the trash. The etag of the file can be included as an ‘If-Match’ header to prevent race conditions.
        /// <remarks>Depending on the enterprise settings for this user, the item will either be actually deleted from Box or moved to the trash.</remarks>
        /// </summary>
        /// <param name="id">Id of the file</param>
        /// <param name="etag">The etag of the file. This is in the ‘etag’ field of the file object.</param>
        /// <returns>True - if file is deleted</returns>
        public async Task<bool> DeleteAsync(string id, string etag = null)
        {
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, id)
                .Method(RequestMethod.Delete)
                .Header("If-Match", etag);

            IBoxResponse<BoxFile> response = await ToResponseAsync<BoxFile>(request).ConfigureAwait(false);

            return response.Status == ResponseStatus.Success;
        }

        /// <summary>
        /// Used to create a copy of a file in another folder. The original version of the file will not be altered.
        /// </summary>
        /// <param name="fileId">The ID of source file.</param>
        /// <param name="folderId">The ID of destianation folder.</param>
        /// <param name="name">An optional new name for the file. Default value is null.</param>
        /// <param name="fields">Fields which shall be returned in result.</param>
        /// <returns>A full file object is returned if the ID is valid and if the update is successful. 
        /// Errors can be thrown if the destination folder is invalid or if a file-name collision occurs. </returns>
        public async Task<BoxFile> CopyAsync(string fileId, string folderId, string name = null, List<string> fields = null)
        {
            fileId.ThrowIfNullOrWhiteSpace("fileId");
            folderId.ThrowIfNullOrWhiteSpace("folderId");

            BoxFileRequest fileRequest = new BoxFileRequest()
            {
                Name = name,
                Parent = new BoxRequestEntity() { Id = folderId }
            };

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, string.Format(Constants.CopyPathString, fileId))
                .Method(RequestMethod.Post)
                .Param(ParamFields, fields)
                .Payload(_converter.Serialize(fileRequest));

            IBoxResponse<BoxFile> response = await ToResponseAsync<BoxFile>(request).ConfigureAwait(false);

            return response.ResponseObject;
        }

        /// <summary>
        /// Used to create a shared link for this particular file. Please see here for more information on the permissions available for shared links. 
        /// </summary>
        /// <param name="id">The ID of file.</param>
        /// <param name="sharedLinkRequest">Shared link request data which shall contain:
        /// sharedLinkRequest.Access - The level of access required for this shared link. Can be open, company, collaborators, or null to get default share level.
        /// sharedLinkRequest.UnsharedAt - The day that this link should be disabled at. Timestamps are rounded off to the given day. This field can only be set if the user is not a free user.
        /// sharedLinkRequest.Password - The password to require before viewing this link.
        /// sharedLinkRequest.Permissions.Download - Whether this link allows downloads.
        /// sharedLinkRequest.Permissions.Preview - Whether this link allows previewing. Can only be used with open and company.
        /// sharedLinkRequest.EffectiveAccess - The access level set by the enterprise administrator. This will override any previous access levels set for the shared link and prevent any less-restrictive access levels to be set.</param>
        /// <param name="fields">Filds which shall be returned in result.</param>        
        /// <returns>A full file object containing the updated shared link is returned if the ID is valid and if the update is successful.</returns>
        public async Task<BoxFile> CreateSharedLinkAsync(string id, BoxSharedLinkRequest sharedLinkRequest, List<string> fields = null)
        {
            id.ThrowIfNullOrWhiteSpace("id");
            sharedLinkRequest.ThrowIfNull("sharedLinkRequest");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, id)
                .Method(RequestMethod.Put)
                .Param(ParamFields, fields)
                .Payload(_converter.Serialize(new BoxItemRequest() { SharedLink = sharedLinkRequest }));

            IBoxResponse<BoxFile> response = await ToResponseAsync<BoxFile>(request).ConfigureAwait(false);

            return response.ResponseObject;
        }

        /// <summary>
        /// Used to delete the shared link for this particular file.
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public async Task<BoxFile> DeleteSharedLinkAsync(string id)
        {
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, id)
                .Method(RequestMethod.Put)
                .Payload(_converter.Serialize(new BoxDeleteSharedLinkRequest()));

            IBoxResponse<BoxFile> response = await ToResponseAsync<BoxFile>(request).ConfigureAwait(false);

            return response.ResponseObject;
        }

        /// <summary>
        /// Retrieves the comments on a particular file, if any exist.
        /// </summary>
        /// <param name="id">The Id of the item the comments should be retrieved for</param>
        /// <returns>A Collection of comment objects are returned. If there are no comments on the file, an empty comments array is returned</returns>
        public async Task<BoxCollection<BoxComment>> GetCommentsAsync(string id, List<string> fields = null)
        {
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, string.Format(Constants.CommentsPathString, id))
                .Param(ParamFields, fields);

            IBoxResponse<BoxCollection<BoxComment>> response = await ToResponseAsync<BoxCollection<BoxComment>>(request).ConfigureAwait(false);

            return response.ResponseObject;
        }

        /// <summary>
        /// Retrieves a thumbnail, or smaller image representation, of this file. Sizes of 32x32,
        ///64x64, 128x128, and 256x256 can be returned in the .png format and sizes of 32x32, 94x94, 160x160, and 320x320 can be returned in the .jpg format.
        /// Thumbnails can be generated for the image and video file formats listed here.
        /// <see cref="http://community.box.com/t5/Managing-Your-Content/What-file-types-are-supported-by-Box-s-Content-Preview/ta-p/327"/>
        /// </summary>
        /// <param name="id">Id of the file</param>
        /// <param name="minHeight">The minimum height of the thumbnail</param>
        /// <param name="minWidth">The minimum width of the thumbnail</param>
        /// <param name="maxHeight">The maximum height of the thumbnail</param>
        /// <param name="maxWidth">The maximum width of the thumbnail</param>
        /// <param name="handleRetry">specifies whether the method handles retries. If true, then the method would retry the call if the HTTP response is 'Accepted'. The delay for the retry is determined 
        /// by the RetryAfter header, or if that header is not set, by the constant DefaultRetryDelay</param>
        /// <param name="throttle">Whether the requests will be throttled. Recommended to be left true to prevent spamming the server</param>
        /// <returns></returns>
        public async Task<Stream> GetThumbnailAsync(string id, int? minHeight = null, int? minWidth = null, int? maxHeight = null, int? maxWidth = null, bool throttle = true, bool handleRetry = true)
        {
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, string.Format(Constants.ThumbnailPathString, id))
                .Param("min_height", minHeight.ToString())
                .Param("min_width", minWidth.ToString())
                .Param("max_height", maxHeight.ToString())
                .Param("max_width", maxWidth.ToString());

            IBoxResponse<Stream> response = await ToResponseAsync<Stream>(request, throttle).ConfigureAwait(false);

            while (response.StatusCode == HttpStatusCode.Accepted && handleRetry)
            {
                await TaskEx.Delay(GetTimeDelay(response.Headers));
                response = await ToResponseAsync<Stream>(request, throttle).ConfigureAwait(false);
            }

            return response.ResponseObject;
        }

        /// <summary>
        /// Gets a preview link (URI) for a file that is valid for 60 seconds
        /// </summary>
        /// <param name="id">Id of the file</param>
        /// <returns>Preview link (URI) for a file that is valid for 60 seconds</returns>
        public async Task<Uri> GetPreviewLinkAsync(string id)
        {
            var fields = new List<string>() { "expiring_embed_link" };
            var file = await GetInformationAsync(id, fields);
            return file.ExpiringEmbedLink.Url;
        }

        /// <summary>
        /// Gets the stream of a preview page
        /// </summary>
        /// <param name="id"></param>
        /// <param name="page"></param>
        /// /// <param name="handleRetry"></param>
        /// <returns>A PNG of the preview</returns>
        public async Task<Stream> GetPreviewAsync(string id, int page, bool handleRetry = true)
        {
            return (await GetPreviewResponseAsync(id, page, handleRetry: handleRetry)).ResponseObject;
        }

        /// <summary>
        /// Get the preview and return a BoxFilePreview response. 
        /// </summary>
        /// <param name="id">id of the file to return</param>
        /// <param name="page">page number of the file</param>
        /// <param name="handleRetry">specifies whether the method handles retries. If true, then the method would retry the call if the HTTP response is 'Accepted'. The delay for the retry is determined 
        /// by the RetryAfter header, or if that header is not set, by the constant DefaultRetryDelay</param>
        /// <returns>BoxFilePreview that contains the stream, current page number and total number of pages in the file.</returns>
        public async Task<BoxFilePreview> GetFilePreviewAsync(string id, int page, int? maxWidth = null, int? minWidth = null, int? maxHeight = null, int? minHeight = null, bool handleRetry = true)
        {
            IBoxResponse<Stream> response = await GetPreviewResponseAsync(id, page, maxWidth, minWidth, maxHeight, minHeight, handleRetry);

            BoxFilePreview filePreview = new BoxFilePreview();
            filePreview.CurrentPage = page;
            filePreview.ReturnedStatusCode = response.StatusCode;

            if (response.StatusCode == HttpStatusCode.OK)
            {
                filePreview.PreviewStream = response.ResponseObject;
                filePreview.TotalPages = response.BuildPagesCount();
            }

            return filePreview;
        }

        private async Task<IBoxResponse<Stream>> GetPreviewResponseAsync(string id, int page, int? maxWidth = null, int? minWidth = null, int? maxHeight = null, int? minHeight = null, bool handleRetry = true)
        {
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, string.Format(Constants.PreviewPathString, id))
                .Param("page", page.ToString())
                .Param("max_width", maxWidth.ToString())
                .Param("max_height", maxHeight.ToString())
                .Param("min_width", minWidth.ToString())
                .Param("min_height", minHeight.ToString());

            var response = await ToResponseAsync<Stream>(request).ConfigureAwait(false);

            while (response.StatusCode == HttpStatusCode.Accepted && handleRetry)
            {
                await TaskEx.Delay(GetTimeDelay(response.Headers));
                response = await ToResponseAsync<Stream>(request).ConfigureAwait(false);
            }

            return response;
        }

        /// <summary>
        /// Return the time to wait until retrying the call. If no RetryAfter value is specified in the header, use default value;
        /// </summary>
        private int GetTimeDelay(HttpResponseHeaders headers)
        {
            int timeToWait;
            if (headers != null && headers.RetryAfter != null && int.TryParse(headers.RetryAfter.ToString(), out timeToWait))
                return timeToWait * 1000;

            return Constants.DefaultRetryDelay;
        }

        /// <summary>
        /// Retrieves an item that has been moved to the trash.
        /// </summary>
        /// <param name="id">Id of the file</param>
        /// <returns>The full item will be returned, including information about when the it was moved to the trash.</returns>
        public async Task<BoxFile> GetTrashedAsync(string id, List<string> fields = null)
        {
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, string.Format(Constants.TrashPathString, id))
                .Param(ParamFields, fields);

            IBoxResponse<BoxFile> response = await ToResponseAsync<BoxFile>(request).ConfigureAwait(false);

            return response.ResponseObject;
        }

        /// <summary>
        /// Restores an item that has been moved to the trash. Default behavior is to restore the item to the folder it was in before 
        /// it was moved to the trash. If that parent folder no longer exists or if there is now an item with the same name in that 
        /// parent folder, the new parent folder and/or new name will need to be included in the request.
        /// </summary>
        /// <param name="fileRequest">Fill required inputs: Name  - The new name for this item, Id - id of the file. Optional input: Parent - The new parent folder for this item </param>
        /// <param name="fields">Fields in result</param>
        /// <returns>The full item will be returned with a 201 Created status. By default it is restored to the parent folder it was in before it was trashed.</returns>
        public async Task<BoxFile> RestoreTrashedAsync(BoxFileRequest fileRequest, List<string> fields = null)
        {
            fileRequest.ThrowIfNull("fileRequest")
                .Id.ThrowIfNullOrWhiteSpace("fileRequest.Id");
            fileRequest.Name.ThrowIfNullOrWhiteSpace("fileRequest.Name");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, fileRequest.Id)
                .Method(RequestMethod.Post)
                .Param(ParamFields, fields)
                .Payload(_converter.Serialize(fileRequest));

            IBoxResponse<BoxFile> response = await ToResponseAsync<BoxFile>(request).ConfigureAwait(false);

            return response.ResponseObject;
        }

        /// <summary>
        /// Permanently deletes an item that is in the trash. The item will no longer exist in Box. This action cannot be undone.
        /// </summary>
        /// <param name="id">Id of the file</param>
        /// <returns>An empty 204 No Content response will be returned upon successful deletion</returns>
        public async Task<bool> PurgeTrashedAsync(string id)
        {
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, string.Format(Constants.TrashPathString, id))
                .Method(RequestMethod.Delete);

            IBoxResponse<BoxFile> response = await ToResponseAsync<BoxFile>(request).ConfigureAwait(false);

            return response.Status == ResponseStatus.Success;
        }

        /// <summary>
        /// Gets a lock file object representation of the provided file Id
        /// </summary>
        /// <param name="id">Id of file information to retrieve</param>
        /// <returns></returns>
        public async Task<BoxFileLock> GetLockAsync(string id)
        {
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, id)
                .Param(ParamFields, BoxFile.FieldLock);

            IBoxResponse<BoxFile> response = await ToResponseAsync<BoxFile>(request).ConfigureAwait(false);

            return response.ResponseObject.Lock;
        }

        /// <summary>
        /// To lock and unlock files, set or clear the lock properties on the file.
        /// </summary>
        /// <param name="lockFileRequest">Request contains Lock object for setting of lock properties such as ExpiresAt - the time the lock expires, IsDownloadPrevented - whether or not the file can be downloaded while locked. </param>
        /// <param name="id">Id of the file</param>
        /// <returns>Returns information about locked file</returns>
        public async Task<BoxFileLock> UpdateLockAsync(BoxFileLockRequest lockFileRequest, string id)
        {
            lockFileRequest.ThrowIfNull("lockFileRequest");
            lockFileRequest.Lock.ThrowIfNull("lockFileRequest.Lock");
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, id)
                .Method(RequestMethod.Put)
                .Param(ParamFields, "lock");

            request.Payload = _converter.Serialize(lockFileRequest);

            IBoxResponse<BoxFile> response = await ToResponseAsync<BoxFile>(request).ConfigureAwait(false);

            return response.ResponseObject.Lock;
        }

        /// <summary>
        /// Remove a lock
        /// </summary>
        /// <param name="id"></param>
        /// <returns></returns>
        public async Task<bool> UnLock(string id)
        {
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, id)
                .Method(RequestMethod.Put)
                .Payload("{\"lock\":null}");

            IBoxResponse<BoxFile> response = await ToResponseAsync<BoxFile>(request).ConfigureAwait(false);

            return response.Status == ResponseStatus.Success;
        }

        /// <summary>
        /// Retrieves all of the tasks for given file.
        /// </summary>
        /// <param name="id">The Id of the file the tasks should be retrieved for</param>
        /// <param name="fields">Attribute(s) to include in the response</param>
        /// <returns>A collection of mini task objects is returned. If there are no tasks, an empty collection will be returned.</returns>
        public async Task<BoxCollection<BoxTask>> GetFileTasks(string id, List<string> fields = null)
        {
            id.ThrowIfNullOrWhiteSpace("id");

            BoxRequest request = new BoxRequest(_config.FilesEndpointUri, string.Format(Constants.TasksPathString, id))
                .Param(ParamFields, fields);


            IBoxResponse<BoxCollection<BoxTask>> response = await ToResponseAsync<BoxCollection<BoxTask>>(request).ConfigureAwait(false);

            return response.ResponseObject;
        }
    }
}
