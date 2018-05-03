﻿using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace VideoProcessor
{
    public static class ProcessVideoOrchestrators
    {
        [FunctionName("O_ProcessVideo")]
        public static async Task<object> ProcessVideo(
            [OrchestrationTrigger] DurableOrchestrationContext ctx,
            TraceWriter log)
        {
            var videoLocation = ctx.GetInput<string>();

            if (!ctx.IsReplaying)
                log.Info("About to call transcode video activity");

            string transcodedLocation = null;
            string thumbnailLocation = null;
            string withIntroLocation = null;
            string approvalResult = "Unknown";
            try
            {
                var transcodeResults =
                    await ctx.CallSubOrchestratorAsync<VideoFileInfo[]>("O_TranscodeVideo", videoLocation);

                transcodedLocation = transcodeResults
                        .OrderByDescending(r => r.BitRate)
                        .Select(r => r.Location)
                        .First();

                if (!ctx.IsReplaying)
                    log.Info("About to call extract thumbnail");

                thumbnailLocation = await
                    ctx.CallActivityAsync<string>("A_ExtractThumbnail", transcodedLocation);

                if (!ctx.IsReplaying)
                    log.Info("About to call prepend intro");

                withIntroLocation = await
                    ctx.CallActivityAsync<string>("A_PrependIntro", transcodedLocation);

                await ctx.CallActivityAsync("A_SendApprovalRequestEmail", withIntroLocation);

                approvalResult = await ctx.WaitForExternalEvent<string>("ApprovalResult");

                if (approvalResult == "Approved")
                {
                    await ctx.CallActivityAsync("A_PublishVideo", withIntroLocation);
                }
                else
                {
                    await ctx.CallActivityAsync("A_RejectVideo", withIntroLocation);
                }
            }
            catch (Exception e)
            {
                if (!ctx.IsReplaying)
                    log.Info($"Caught an error from an activity: {e.Message}");

                await
                    ctx.CallActivityAsync<string>("A_Cleanup", 
                        new[] { transcodedLocation, thumbnailLocation, withIntroLocation });

                return new
                {
                    Error = "Failed to process uploaded video",
                    Message = e.Message
                };
            }

            return new
            {
                Transcoded = transcodedLocation,
                Thumbnail = thumbnailLocation,
                WithIntro = withIntroLocation,
                ApprovalResult = approvalResult
            };

        }

        [FunctionName("O_TranscodeVideo")]
        public static async Task<VideoFileInfo[]> TranscodeVideo(
            [OrchestrationTrigger] DurableOrchestrationContext ctx,
            TraceWriter log)
        {
            var videoLocation = ctx.GetInput<string>();
            var bitRates = await ctx.CallActivityAsync<int[]>("A_GetTranscodeBitrates", null);
            var transcodeTasks = new List<Task<VideoFileInfo>>();

            foreach (var bitRate in bitRates)
            {
                var info = new VideoFileInfo() { Location = videoLocation, BitRate = bitRate };
                var task = ctx.CallActivityAsync<VideoFileInfo>("A_TranscodeVideo", info);
                transcodeTasks.Add(task);
            }

            var transcodeResults = await Task.WhenAll(transcodeTasks);
            return transcodeResults;
        }
    }
}
