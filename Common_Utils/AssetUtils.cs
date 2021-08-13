// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

using Microsoft.Extensions.Configuration;
using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
using Microsoft.Azure.Storage.DataMovement;
using Microsoft.Azure.Storage.Blob;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using System.IO;

namespace Common_Utils
{
    public class AssetUtils
    {


        /// <summary>
        /// Creates the Server side .ism manifest files required to stream an Mp4 file uploaded to an asset with the proper encoding settings. 
        /// </summary>
        /// <returns>
        /// A list of server side manifest files (.ism) created in the Asset folder. Typically this is only going to be a single .ism file. 
        /// </returns>
        public static async Task<IList<string>> CreateServerManifests(IAzureMediaServicesClient client, string resourceGroup, string accountName, Asset inputAsset, StreamingLocator locator)
        {

            // Get the asset associated with the locator.
            Asset asset = client.Assets.Get(resourceGroup, accountName, inputAsset.Name);

            AssetContainerSas response;
            try
            {
                // Create a short lived SAS URL to upload content into the Asset's container.  We use 5 minutes in this sample, but this can be a lot shorter.
                ListContainerSasInput input = new()
                {
                    Permissions = AssetContainerPermission.ReadWriteDelete,
                    ExpiryTime = DateTime.Now.AddMinutes(5).ToUniversalTime()
                };

                response = await client.Assets.ListContainerSasAsync(resourceGroup, accountName, asset.Name, input.Permissions, input.ExpiryTime);
            }
            catch (Exception ex)
            {
                Console.WriteLine("Error when listing blobs of asset '{0}'.", asset.Name);
                Console.WriteLine(ex.Message);
                return null;
            }

            string uploadSasUrl = response.AssetContainerSasUrls.First();
            Uri sasUri = new(uploadSasUrl);
            CloudBlobContainer storageContainer = new(sasUri);

            // Create the Server Manifest .ism file here.  This is a SMIL 2.0 format XML file that points to the uploaded MP4 files in the asset container.
            // This file is required by the Streaming Endpoint to dynamically generate the HLS and DASH streams from the MP4 source file (when properly encoded.)
            GeneratedServerManifest serverManifest = await ServerManifestUtils.LoadAndUpdateManifestTemplateAsync(storageContainer);
            string tempPath = System.IO.Path.GetTempPath();
            string filePath = Path.Combine(tempPath, serverManifest.FileName);

            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            // Load the server manifest .ism and save it to a temp file.
            XDocument doc = XDocument.Parse(serverManifest.Content);
            doc.Save(filePath);

            // Upload the temp .ism file on disk to the Asset's container
            CloudBlockBlob serverManifestBlob = storageContainer.GetBlockBlobReference(Path.GetFileName(filePath));

            // Upload the temp .ism file using the Storage library Data Mover
            await TransferManager.UploadAsync(filePath, serverManifestBlob);

            // Clean up the temp .ism file on disk
            if (File.Exists(filePath))
            {
                File.Delete(filePath);
            }

            // Get a manifest file list from the Storage container.
            // In this sectino we are going to check for the existence of a client manifest and determine if we need to generate a new one. 
            // If one exists, we do not generate it again. 


            string ismcFileName = GetFilesListFromStorage(storageContainer).FirstOrDefault(a => a.ToLower().Contains(".ismc"));

            string ismManifestFileName = GetFilesListFromStorage(storageContainer).FirstOrDefault(a => a.ToLower().EndsWith(".ism"));
            // If there is no .ism then there's no reason to continue.  If there's no .ismc we need to add it.

            if (ismManifestFileName != null && ismcFileName == null)
            {
                Console.WriteLine("Asset {0} : it does not have an ISMC file.", asset.Name);

                // let's try to read client manifest
                XDocument manifest = null;
                try
                {
                    manifest = await GetClientManifest(asset,
                                                       client,
                                                       resourceGroup,
                                                       accountName,
                                                       locator.Name);
                }
                catch (Exception)
                {
                    Console.WriteLine("Error when trying to read client manifest for asset '{0}'.", asset.Name);
                    return null;
                }

                string ismcContentXml = manifest.ToString();
                if (ismcContentXml.Length == 0)
                {
                    Console.WriteLine("Asset {0} : client manifest is empty.", asset.Name);
                    //error state, skip this asset
                    return null;
                }

                if (ismcContentXml.IndexOf("<Protection>") > 0)
                {
                    Console.WriteLine("Asset {0} : content is encrypted. Removing the protection header from the client manifest.", asset.Name);
                    //remove DRM from the ISCM manifest
                    ismcContentXml = XmlManifestUtils.RemoveXmlNode(ismcContentXml);
                }

                string newIsmcFileName = ismManifestFileName.Substring(0, ismManifestFileName.IndexOf(".")) + ".ismc";
                CloudBlockBlob ismcBlob = WriteStringToBlob(ismcContentXml, newIsmcFileName, storageContainer);
                Console.WriteLine("Asset {0} : client manifest created.", asset.Name);

                // Download the ISM so that we can modify it to include the ISMC file link.
                string ismXmlContent = GetFileXmlFromStorage(storageContainer, ismManifestFileName);
                ismXmlContent = XmlManifestUtils.AddIsmcToIsm(ismXmlContent, newIsmcFileName);
                WriteStringToBlob(ismXmlContent, ismManifestFileName, storageContainer);
                Console.WriteLine("Asset {0} : server manifest updated.", asset.Name);

                // update the ism to point to the ismc (download, modify, delete original, upload new)
            }

            // return the .ism manifest
            return GetFilesListFromStorage(storageContainer).Where(a => a.ToLower().EndsWith(".ism")).ToList();
        }


        public static async Task<XDocument> GetClientManifest(Asset asset, IAzureMediaServicesClient client, string resourceGroup, string accountName, string preferredLocatorName = null)
        {
            Uri myuri = (await GetValidOnDemandSmoothURIAsync(asset, client, resourceGroup, accountName, preferredLocatorName)).Item1;

            if (myuri == null)
            {
                myuri = (await GetValidOnDemandSmoothURIAsync(asset, client, resourceGroup, accountName)).Item1;
            }
            if (myuri != null)
            {
                return XDocument.Load(myuri.ToString());
            }
            else
            {
                throw new Exception("Streaming locator is null");
            }
        }

        public static async Task<(Uri, bool)> GetValidOnDemandSmoothURIAsync(Asset asset, IAzureMediaServicesClient client, string resourceGroup, string accountName, string useThisLocatorName = null, LiveOutput liveOutput = null)
        {
            bool emptyLiveOutput = false; // used to signal the live output is empty (do not use ListPathsAsync)

            IList<AssetStreamingLocator> locators = (await client.Assets.ListStreamingLocatorsAsync(resourceGroup, accountName, asset.Name)).StreamingLocators;

            Microsoft.Rest.Azure.IPage<StreamingEndpoint> ses = await client.StreamingEndpoints.ListAsync(resourceGroup, accountName);

            StreamingEndpoint runningSes = ses.Where(s => s.ResourceState == StreamingEndpointResourceState.Running).FirstOrDefault();
            if (runningSes == null)
            {
                runningSes = ses.FirstOrDefault();
            }

            if (locators.Count > 0 && runningSes != null)
            {
                string locatorName = useThisLocatorName ?? locators.First().Name;
                AssetStreamingLocator locatorToUse = locators.Where(l => l.Name == locatorName).First();

                IList<StreamingPath> streamingPaths = (await client.StreamingLocators.ListPathsAsync(resourceGroup, accountName, locatorName)).StreamingPaths;
                IEnumerable<StreamingPath> smoothPath = streamingPaths.Where(p => p.StreamingProtocol == StreamingPolicyStreamingProtocol.SmoothStreaming);
                if (smoothPath.Any(s => s.Paths.Count != 0))
                {
                    UriBuilder uribuilder = new()
                    {
                        Host = runningSes.HostName,
                        Path = smoothPath.FirstOrDefault().Paths.FirstOrDefault()
                    };
                    return (uribuilder.Uri, emptyLiveOutput);
                }
                else if (smoothPath.Any() && liveOutput != null) // A live output with no data in it as live event not started. But we can determine the output URLs
                {
                    UriBuilder uribuilder = new()
                    {
                        Host = runningSes.HostName,
                        Path = locatorToUse.StreamingLocatorId.ToString() + "/" + liveOutput.ManifestName + ".ism/manifest"
                    };
                    emptyLiveOutput = true;
                    return (uribuilder.Uri, emptyLiveOutput);
                }
                else
                {
                    return (null, emptyLiveOutput);
                }
            }
            else
            {
                return (null, emptyLiveOutput);
            }
        }


        private static List<string> GetFilesListFromStorage(CloudBlobContainer storageContainer)
        {
            List<CloudBlockBlob> fullBlobList = storageContainer.ListBlobs().OfType<CloudBlockBlob>().ToList();
            // Filter the list to only contain .ism and .ismc files
            IEnumerable<string> filteredList = from b in fullBlobList
                                               where b.Name.ToLower().Contains(".ism")
                                               select b.Name;
            return filteredList.ToList();
        }

        private static string GetFileXmlFromStorage(CloudBlobContainer storageContainer, string ismManifestFileName)
        {
            CloudBlockBlob blob = storageContainer.GetBlockBlobReference(ismManifestFileName);
            return blob.DownloadText();
        }

        private static CloudBlockBlob WriteStringToBlob(string ContentXml, string fileName, CloudBlobContainer storageContainer)
        {
            CloudBlockBlob newBlob = storageContainer.GetBlockBlobReference(fileName);
            newBlob.UploadText(ContentXml);
            return newBlob;
        }


    }

}