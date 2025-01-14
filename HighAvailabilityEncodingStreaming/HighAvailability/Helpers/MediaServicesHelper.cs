﻿// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

namespace HighAvailability.Helpers
{
    using Microsoft.Azure.EventGrid.Models;
    using Microsoft.Azure.Management.Media;
    using Microsoft.Azure.Management.Media.Models;
    using Microsoft.IdentityModel.Tokens;
    using System;
    using System.Collections.Generic;
    using System.IdentityModel.Tokens.Jwt;
    using System.Security.Claims;
    using System.Threading.Tasks;

    /// <summary>
    /// Implements helper methods for Azure Media Services instance client
    /// </summary>
    public static class MediaServicesHelper
    {
        /// <summary>
        /// Checks if transform exists, if not, creates transform
        /// </summary>
        /// <param name="client">Azure Media Services instance client</param>
        /// <param name="resourceGroupName">Azure resource group</param>
        /// <param name="accountName">Azure Media Services instance account name</param>
        /// <param name="transformName">Transform name</param>
        /// <param name="preset">transform preset object</param>
        /// <returns></returns>
        public static async Task<Transform> EnsureTransformExists(IAzureMediaServicesClient client, string resourceGroupName, string accountName, string transformName, Preset preset)
        {
            if (client == null)
            {
                throw new ArgumentNullException(nameof(client));
            }

            // create output with given preset
            var outputs = new TransformOutput[]
            {
                new TransformOutput(preset)
            };

            // create new transform
            Transform transform = await client.Transforms.CreateOrUpdateAsync(resourceGroupName, accountName, transformName, outputs).ConfigureAwait(false);

            return transform;
        }

        /// <summary>
        /// Checks if content key policy exists, if not, creates new one
        /// This code is based on https://github.com/Azure-Samples/media-services-v3-dotnet-core-tutorials/tree/master/NETCore/EncodeHTTPAndPublishAESEncrypted
        /// </summary>
        /// <param name="client">Azure Media Services instance client</param>
        /// <param name="resourceGroup">Azure resource group</param>
        /// <param name="accountName">Azure Media Services instance account name</param>
        /// <param name="contentKeyPolicyName">Content key policy name</param>
        /// <param name="tokenSigningKey">Token signing key</param>
        /// <param name="issuer">Token issuer</param>
        /// <param name="audience">Token audience</param>
        /// <returns></returns>
        public static async Task<ContentKeyPolicy> EnsureContentKeyPolicyExists(IAzureMediaServicesClient client, string resourceGroup, string accountName, string contentKeyPolicyName, byte[] tokenSigningKey, string issuer, string audience)
        {
            var primaryKey = new ContentKeyPolicySymmetricTokenKey(tokenSigningKey);
            List<ContentKeyPolicyRestrictionTokenKey> alternateKeys = null;
            var requiredClaims = new List<ContentKeyPolicyTokenClaim>()
            {
                ContentKeyPolicyTokenClaim.ContentKeyIdentifierClaim
            };

            var options = new List<ContentKeyPolicyOption>()
            {
                new ContentKeyPolicyOption(
                    new ContentKeyPolicyClearKeyConfiguration(),
                    new ContentKeyPolicyTokenRestriction(issuer, audience, primaryKey,
                        ContentKeyPolicyRestrictionTokenType.Jwt, alternateKeys, requiredClaims))
            };

            var policy = await client.ContentKeyPolicies.CreateOrUpdateAsync(resourceGroup, accountName, contentKeyPolicyName, options).ConfigureAwait(false);

            return policy;
        }

        /// <summary>
        /// Gets token to for a given key identifier and key
        /// This code is based on https://github.com/Azure-Samples/media-services-v3-dotnet-core-tutorials/tree/master/NETCore/EncodeHTTPAndPublishAESEncrypted
        /// </summary>
        /// <param name="issuer">Token issuer</param>
        /// <param name="audience">Token audience</param>
        /// <param name="keyIdentifier">key identifier</param>
        /// <param name="tokenVerificationKey">binary key</param>
        /// <returns></returns>
        public static string GetToken(string issuer, string audience, string keyIdentifier, byte[] tokenVerificationKey)
        {
            var tokenSigningKey = new SymmetricSecurityKey(tokenVerificationKey);

            var cred = new SigningCredentials(
                tokenSigningKey,
                // Use the  HmacSha256 and not the HmacSha256Signature option, or the token will not work!
                SecurityAlgorithms.HmacSha256,
                SecurityAlgorithms.Sha256Digest);

            var claims = new Claim[]
            {
                new Claim(ContentKeyPolicyTokenClaim.ContentKeyIdentifierClaim.ClaimType, keyIdentifier)
            };

            var token = new JwtSecurityToken(
                issuer: issuer,
                audience: audience,
                claims: claims,
                notBefore: DateTime.Now.AddMinutes(-5),
                expires: DateTime.Now.AddMinutes(60),
                signingCredentials: cred);

            var handler = new JwtSecurityTokenHandler();

            return handler.WriteToken(token);
        }

        /// <summary>
        /// Determines if failed job should be resubmitted
        /// </summary>
        /// <param name="job">Azure Media Services job</param>
        /// <param name="jobOutputAssetName">Output asset name</param>
        /// <returns>true if job should be resubmitted</returns>
        public static bool HasRetriableError(Job job, string jobOutputAssetName)
        {
            // if overall job has failed
            if (job.State == JobState.Error)
            {
                // find job output associated with specific asset name
                foreach (var jobOutput in job.Outputs)
                {
                    if (jobOutput is JobOutputAsset)
                    {
                        var jobOutputAsset = (JobOutputAsset)jobOutput;
                        if (jobOutputAsset.State == JobState.Error && jobOutputAsset.AssetName.Equals(jobOutputAssetName, StringComparison.InvariantCultureIgnoreCase))
                        {
                            // check if job should be retried
                            if (jobOutputAsset.Error.Retry == JobRetry.MayRetry)
                            {
                                return true;
                            }
                        }
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// Returns job state for specific asset
        /// </summary>
        /// <param name="job">Azure Media Services job</param>
        /// <param name="jobOutputAssetName">asset name</param>
        /// <returns>JobState and timestamp for a given asset name</returns>
        public static (JobState, DateTimeOffset) GetJobOutputState(Job job, string jobOutputAssetName)
        {
            foreach (var jobOutput in job.Outputs)
            {
                if (jobOutput is JobOutputAsset)
                {
                    var jobOutputAsset = (JobOutputAsset)jobOutput;
                    if (jobOutputAsset.AssetName.Equals(jobOutputAssetName, StringComparison.InvariantCultureIgnoreCase))
                    {
                        DateTimeOffset statusTime = jobOutputAsset.EndTime ?? jobOutputAsset.StartTime ?? job.LastModified;
                        return (jobOutputAsset.State, statusTime);
                    }
                }
            }

            return (null, DateTime.MinValue);
        }

        /// <summary>
        /// Determines if failed job should be resubmitted using EventGrid event data
        /// </summary>
        /// <param name="jobOutput">Job output from EventGrid event</param>
        /// <returns>true if job should be resubmitted</returns>
        public static bool HasRetriableError(MediaJobOutputAsset jobOutput)
        {
            if (jobOutput.State == MediaJobState.Error)
            {
                if (jobOutput.Error.Retry == MediaJobRetry.MayRetry)
                {
                    return true;
                }
            }

            return false;
        }
    }
}
