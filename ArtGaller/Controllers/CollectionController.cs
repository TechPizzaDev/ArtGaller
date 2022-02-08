using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net;
using System.Security.Authentication;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using ArtGaller.Data;
using ArtGaller.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;

namespace ArtGaller.Controllers
{
    [Authorize]
    public class CollectionController : Controller
    {
        public const string UserDataStorageRoot = "C:/ArtGaller/UserData";
        public const string UploadsDirectory = "Uploads";
        public const string ThumbnailDirectory = "Thumbnails";

        public const int MaxViewCount = 200;
        public const int FileCacheDuration = 60 * 60 * 24 * 7;
        public const long ByteUploadLimit = 1024 * 1024 * 1024 * 4L;

        private readonly ApplicationDbContext _dbContext;
        private readonly SignInManager<IdentityUser> _signInManager;
        private readonly ILogger<CollectionController> _logger;

        private FileExtensionContentTypeProvider _contentTypeProvider = new();

        public CollectionController(
            ApplicationDbContext dbContext,
            SignInManager<IdentityUser> signInManager,
            ILogger<CollectionController> logger)
        {
            _dbContext = dbContext;
            _signInManager = signInManager;
            _logger = logger;
        }

        private UserResult<string> FindUserId()
        {
            string userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (userId == null)
                return new(Unauthorized());

            return new UserResult<string>(userId, userId);
        }

        public IActionResult Index(int? offset, int? count)
        {
            UserResult<string> userId = FindUserId();
            if (userId.HasError)
                return userId.Error;

            int actualOffset = offset ?? 0;
            if (actualOffset < 0)
                throw new ArgumentOutOfRangeException(nameof(offset), offset, null);

            int actualCount = count ?? 50;
            if (actualCount < 0)
                throw new ArgumentOutOfRangeException(nameof(count), count, null);
            if (actualCount > MaxViewCount)
                actualCount = MaxViewCount;

            string userIdItem = userId.Item;
            var uploadInfos = _dbContext.UploadInfos
                .Where(x => x.UserId == userIdItem)
                .OrderBy(x => x.CreationTime)
                .Skip(actualOffset)
                .Take(actualCount);

            var items = new List<UploadInfo>(uploadInfos);

            return View(new CollectionViewModel
            {
                Offset = actualOffset,
                Count = actualCount,
                Items = items
            });
        }

        private string GetContentType(UploadInfo uploadInfo)
        {
            string? contentType = uploadInfo.ContentType;
            if (contentType == null)
            {
                string? contentTypeSource = uploadInfo.DisplayName ?? uploadInfo.FormFileName;
                if (!_contentTypeProvider.TryGetContentType(contentTypeSource, out contentType))
                    contentType = null;
            }
            if (contentType == null)
                contentType = "application/octet-stream";

            return contentType;
        }

        private async ValueTask<UserResult<UploadInfo>> FindUploadInfo(
            string uploadId, CancellationToken cancellationToken)
        {
            UserResult<string> userId = FindUserId();
            if (userId.HasError)
                return new(userId.Error);

            if (!Guid.TryParse(uploadId, out Guid uploadGuid))
                return new(NotFound(), userId.Item);

            var keyValues = new object[] { userId.Item, uploadGuid };
            var uploadInfo = await _dbContext.UploadInfos.FindAsync(keyValues, cancellationToken);
            if (uploadInfo == null)
                return new(NotFound(), userId.Item);

            return new(userId.Item, uploadInfo);
        }

        [ResponseCache(NoStore = true)]
        public async ValueTask<IActionResult> Stream(
            string uploadId, CancellationToken cancellationToken)
        {
            UserResult<UploadInfo> uploadInfo = await FindUploadInfo(uploadId, cancellationToken);
            if (uploadInfo.HasError)
                return uploadInfo.Error;

            string userDataPath = GetUserDataPath(uploadInfo.UserId);
            string uploadPath = Path.Combine(userDataPath, UploadsDirectory, uploadInfo.Item.FileName);
            if (!System.IO.File.Exists(uploadPath))
                return NotFound();

            string contentDispositionValue = ContentDispositionUtil.GetHeaderValue(
                uploadInfo.Item.DownloadName, inline: true);
            Response.Headers.Add("Content-Disposition", contentDispositionValue);

            string contentType = GetContentType(uploadInfo.Item);

            var fs = new FileStream(
                uploadPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 80, useAsync: true);

            var result = new FileStreamResult(fs, contentType)
            {
                EnableRangeProcessing = true,
                LastModified = uploadInfo.Item.CreationTime,
            };

            Response.RegisterForDisposeAsync(fs);
            return result;
        }

        [ResponseCache(Duration = FileCacheDuration, Location = ResponseCacheLocation.Any)]
        public async ValueTask<IActionResult> Thumbnail(
            string uploadId, CancellationToken cancellationToken)
        {
            UserResult<UploadInfo> uploadInfo = await FindUploadInfo(uploadId, cancellationToken);
            if (uploadInfo.HasError)
                return uploadInfo.Error;

            string userDataPath = GetUserDataPath(uploadInfo.UserId);
            string uploadPath = Path.Combine(userDataPath, UploadsDirectory, uploadInfo.Item.FileName);
            string thumbnailPath = Path.Combine(userDataPath, ThumbnailDirectory, uploadInfo.Item.FileName);

            string streamPath;
            if (System.IO.File.Exists(thumbnailPath))
            {
                streamPath = thumbnailPath;
            }
            else if (System.IO.File.Exists(uploadPath))
            {
                streamPath = uploadPath;
            }
            else
            {
                return NotFound();
            }

            string contentType = GetContentType(uploadInfo.Item);

            var fs = new FileStream(
                streamPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 80, useAsync: true);

            Response.RegisterForDisposeAsync(fs);
            return File(fs, contentType);
        }

        [HttpPost]
        public async ValueTask<IActionResult> Delete(
            string uploadId, CancellationToken cancellationToken)
        {
            UserResult<UploadInfo> uploadInfo = await FindUploadInfo(uploadId, cancellationToken);
            if (uploadInfo.HasError)
                return uploadInfo.Error;

            using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                _dbContext.Remove(uploadInfo.Item);

                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);

                string userDataPath = GetUserDataPath(uploadInfo.UserId);

                string thumbnailPath = Path.Combine(userDataPath, ThumbnailDirectory, uploadInfo.Item.FileName);
                if (System.IO.File.Exists(thumbnailPath))
                    System.IO.File.Delete(thumbnailPath);

                string uploadPath = Path.Combine(userDataPath, UploadsDirectory, uploadInfo.Item.FileName);
                if (System.IO.File.Exists(uploadPath))
                    System.IO.File.Delete(uploadPath);

                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None);

                if (!(ex is OperationCanceledException))
                    throw;

                return Error();
            }
        }

        public IActionResult Search(string query)
        {
            return View(new SearchViewModel() { Query = query });
        }

        [ResponseCache(NoStore = true)]
        public IActionResult Error()
        {
            return View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
        }

        public static string GetUserDataPath(string userId)
        {
            string userPath = Path.Combine(UserDataStorageRoot, userId);
            return userPath;
        }

        private long MultipartBoundaryLengthLimit = 1024 * 1024 * 1024 * 64L;

        [HttpPost]
        [DisableFormValueModelBinding]
        //[ValidateAntiForgeryToken]
        //[RequestSizeLimit(int.MaxValue)]
        public async Task<IActionResult> FileUpload(CancellationToken cancellationToken)
        {
            if (!MultipartRequestHelper.IsMultipartContentType(Request.ContentType))
            {
                ModelState.AddModelError("File", $"The request couldn't be processed (Error 1).");
                return BadRequest(ModelState);
            }

            UserResult<string> userId = FindUserId();
            if (userId.HasError)
                return userId.Error;

            string userDataPath = GetUserDataPath(userId.Item);
            string uploadsPath = Path.Combine(userDataPath, UploadsDirectory);
            Directory.CreateDirectory(uploadsPath);

            // Accumulate the form data key-value pairs in the request (formAccumulator).
            var formAccumulator = new KeyValueAccumulator();

            var createdFiles = new List<string>();

            var boundary = MultipartRequestHelper.GetBoundary(
                MediaTypeHeaderValue.Parse(Request.ContentType),
                100);

            MultipartReader reader = new(boundary, HttpContext.Request.Body);
            reader.BodyLengthLimit = ByteUploadLimit;

            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                MultipartSection? section = await reader.ReadNextSectionAsync(cancellationToken);
                while (section != null)
                {
                    if (ContentDispositionHeaderValue.TryParse(
                        section.ContentDisposition,
                        out ContentDispositionHeaderValue? contentDisposition))
                    {
                        if (MultipartRequestHelper.HasFileContentDisposition(contentDisposition))
                        {
                            // Don't trust the file name sent by the client. 
                            // To display the file name, HTML-encode the value.
                            //string trustedFileNameForDisplay = WebUtility.HtmlEncode(
                            //    contentDisposition.FileName.Value);

                            //streamedFileContent =
                            //    await FileHelpers.ProcessStreamedFile(section, contentDisposition,
                            //        ModelState, _permittedExtensions, _fileSizeLimit);

                            string fileName;
                            string filePath;
                            do
                            {
                                fileName = Path.GetRandomFileName();
                                filePath = Path.Combine(uploadsPath, fileName);
                            }
                            while (System.IO.File.Exists(filePath));

                            createdFiles.Add(filePath);

                            await using (var fileStream = new FileStream(
                                filePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 4,
                                FileOptions.Asynchronous | FileOptions.SequentialScan))
                            {
                                await section.Body.CopyToAsync(fileStream, cancellationToken);
                            }

                            string formFileName = contentDisposition.FileName.Value;
                            var uploadInfo = new UploadInfo
                            {
                                UserId = userId.Item,
                                FileName = fileName,
                                FormFileName = formFileName,
                                CreationTime = DateTimeOffset.UtcNow,
                            };

                            _dbContext.UploadInfos.Add(uploadInfo);

                            if (!ModelState.IsValid)
                            {
                                return BadRequest(ModelState);
                            }
                        }
                        else if (MultipartRequestHelper.HasFormDataContentDisposition(contentDisposition))
                        {
                            // Don't limit the key name length because the 
                            // multipart headers length limit is already in effect.
                            string key = HeaderUtilities
                                .RemoveQuotes(contentDisposition.Name).Value;

                            using var streamReader = new StreamReader(
                                section.Body,
                                detectEncodingFromByteOrderMarks: true,
                                bufferSize: 1024,
                                leaveOpen: true);

                            // The value length limit is enforced by 
                            // MultipartBodyLengthLimit
                            string? value = await streamReader.ReadToEndAsync();

                            if (string.Equals(
                                value, "undefined", StringComparison.OrdinalIgnoreCase))
                            {
                                value = string.Empty;
                            }

                            formAccumulator.Append(key, value);

                            //if (formAccumulator.ValueCount >
                            //    _defaultFormOptions.ValueCountLimit)
                            //{
                            //    // Form key count limit of 
                            //    // _defaultFormOptions.ValueCountLimit 
                            //    // is exceeded.
                            //    ModelState.AddModelError("File",
                            //        $"The request couldn't be processed (Error 3).");
                            //    // Log error
                            //
                            //    return BadRequest(ModelState);
                            //}
                        }
                    }

                    // Drain any remaining section body that hasn't been consumed and
                    // read the headers for the next section.
                    section = await reader.ReadNextSectionAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                await _dbContext.SaveChangesAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None);

                foreach (string file in createdFiles)
                    System.IO.File.Delete(file);

                if (!(ex is OperationCanceledException))
                    throw;
            }

            /*
            var createdFiles = new List<string>();

            using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                foreach (IFormFile formFile in form.Files)
                {
                    PickFileName:
                    string fileName = Path.GetRandomFileName();
                    string filePath = Path.Combine(uploadsPath, fileName);
                    if (System.IO.File.Exists(filePath))
                        goto PickFileName;
            
                    createdFiles.Add(filePath);
                    await using (var fileStream = new FileStream(
                        filePath, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 64, useAsync: true))
                    {
                        await formFile.CopyToAsync(fileStream, cancellationToken);
                    }

                    var uploadInfo = new UploadInfo
                    {
                        UserId = userId,
                        FileName = fileName,
                        FormFileName = formFile.FileName,
                        ContentType = formFile.ContentType,
                        CreationTime = DateTimeOffset.UtcNow
                    };

                    _dbContext.UploadInfos.Add(uploadInfo);
                }

                await _dbContext.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                await transaction.RollbackAsync(CancellationToken.None);

                foreach (string file in createdFiles)
                    System.IO.File.Delete(file);

                if (!(ex is OperationCanceledException))
                    throw;
            }
            */

            return View();
        }

        public readonly struct UserResult<T>
        {
            public IActionResult? Error { get; }
            public string? UserId { get; }
            public T Item { get; }

            [MemberNotNullWhen(false, nameof(UserId), nameof(Item))]
            [MemberNotNullWhen(true, nameof(Error))]
            public bool HasError => Error != null;

            public UserResult(string userId, T item)
            {
                Error = null;
                UserId = userId ?? throw new ArgumentNullException(nameof(userId));
                Item = item ?? throw new ArgumentNullException(nameof(item));
            }

            public UserResult(IActionResult error, string? userId = default)
            {
                Error = error ?? throw new ArgumentNullException(nameof(error));
                UserId = userId;
                Item = default!;
            }
        }
    }
}
