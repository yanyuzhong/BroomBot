using System.Collections.Generic;
using Microsoft.TeamFoundation.SourceControl.WebApi;
using Microsoft.TeamFoundation.Core.WebApi;
using System.Threading.Tasks;
using System.Linq;
using System;
using System.Net.Http;
using BroomBot.Model;
using System.Net.Http.Headers;
using System.Web;
using static System.Net.WebRequestMethods;
using System.Text;
using Microsoft.VisualStudio.Services.Organization.Client;
using System.IO;
using Newtonsoft.Json;

namespace BroomBot
{
    public static class BroomBotUtils
    {
        public static async Task<IList<GitPullRequest>> GetPullRequests(
            GitHttpClient gitClient,
            string project)
        {
            List<GitPullRequest> pullCollection = new List<GitPullRequest>();
            List<GitRepository> repos = await gitClient.GetRepositoriesAsync(project);
            GitPullRequestSearchCriteria searchCriteria = new GitPullRequestSearchCriteria
            {
                Status = PullRequestStatus.Active
            };

            foreach (GitRepository repo in repos)
            {
                var pulls = await gitClient.GetPullRequestsAsync(project, repo.Id, searchCriteria);

                foreach (var pull in pulls)
                {
                    pullCollection.Add(pull);
                }
            }

            return pullCollection;
        }

        public static async Task<Dictionary<GitPullRequest, bool>> CheckPullRequestFreshness(
            GitHttpClient gitClient,
            string project,
            IList<GitPullRequest> pullRequests,
            DateTime staleDate,
            string botId)
        {
            Dictionary<GitPullRequest, bool> pullCollection = new Dictionary<GitPullRequest, bool>();

            foreach (GitPullRequest pr in pullRequests)
            {
                IList<GitPullRequestCommentThread> threads = await gitClient.GetThreadsAsync(project, pr.Repository.Id, pr.PullRequestId);

                GitPullRequestCommentThread lastUpdated = threads.OrderByDescending(p => p.LastUpdatedDate).FirstOrDefault();

                // Stale PRs have never been updated, or haven't been updated since the staleDate
                if (lastUpdated == null || lastUpdated.LastUpdatedDate < staleDate)
                {
                    // Knowing whether or not the last comment was from the bot will tell us if we need to check for tags or not
                    bool commentByBot = lastUpdated != null && lastUpdated.Comments.Last().Author.Id == botId;
                    pullCollection.Add(pr, commentByBot);
                }
            }

            return pullCollection;
        }

        public static async Task<Dictionary<GitPullRequestCommentThread, string>> CheckPullRequestComments(GitHttpClient gitClient,
            string organization,
            string project,
            IList<GitPullRequest> pullRequests, string botId, HashSet<string> keywords, string token, HttpClient client)
        {
            Dictionary<GitPullRequestCommentThread, string> pullCollection = new Dictionary<GitPullRequestCommentThread, string>();


            foreach (GitPullRequest pr in pullRequests)
            {
                IList<GitPullRequestCommentThread> threads = await gitClient.GetThreadsAsync(project, pr.Repository.Id, pr.PullRequestId);

                foreach (GitPullRequestCommentThread thread in threads)
                {
                    if (thread.Comments.Last().Author.Id != botId)
                    {
                        string comment = thread.Comments.Last().Content;

                        foreach (string keyword in keywords)
                        {
                            int startIdx = comment.IndexOf(keyword, StringComparison.OrdinalIgnoreCase);
                            if (startIdx >= 0)
                            {
                                // create the work item
                                pullCollection.Add(thread, comment[startIdx..]);
                                WorkItemCreateResponse res = await BroomBotUtils.CreateWorkItem(client, organization, project, "Task", token, comment[startIdx..]);

                                string commentMessage = $"Work item created by bot. See link: {res.Link.html.href}";
                                Comment botComment = new Comment { Content = commentMessage };
                                List<Comment> commentList = new List<Comment> { botComment };
                                GitPullRequestCommentThread commentThread = new GitPullRequestCommentThread
                                {
                                    Comments = commentList,
                                    Status = CommentThreadStatus.Active
                                };
                                await gitClient.CreateThreadAsync(commentThread, pr.Repository.Id, pr.PullRequestId);
                                break;
                            }
                        }
                    }
                } 
            }

            return pullCollection;
        } 

        public static async Task<IList<GitPullRequest>> TagStalePullRequests(
            GitHttpClient gitClient,
            string project,
            Dictionary<GitPullRequest, bool> stalePRs,
            string warningPrefix,
            int warningCount,
            string warningMessage,
            string abandonMessage)
        {
            List<GitPullRequest> pullCollection = new List<GitPullRequest>();
            string commentMessage = string.Empty;

            foreach (KeyValuePair<GitPullRequest, bool> pr in stalePRs)
            {
                // Retrieve the tags so we can figure out what we need to remove, how many warnings have been given
                List<WebApiTagDefinition> allTags = await gitClient.GetPullRequestLabelsAsync(project, pr.Key.Repository.Id, pr.Key.PullRequestId);

                if (!pr.Value)
                {
                    // PR has been updated since the last bot action, remove tags
                    List<WebApiTagDefinition> tagsToRemove = allTags.Where(t => t.Name.StartsWith(warningPrefix)).ToList();

                    foreach (WebApiTagDefinition tag in tagsToRemove)
                    {
                        await gitClient.DeletePullRequestLabelsAsync(project, pr.Key.Repository.Id, pr.Key.PullRequestId, tag.Id.ToString());
                    }

                    commentMessage = warningMessage;
                }
                else
                {
                    // check how many tags are already on it, if it's more than the warning threshold, add it to the list of returned PRs
                    int botTagCount = allTags.Where(t => t.Name.StartsWith(warningPrefix)).Count();

                    if (botTagCount >= warningCount)
                    {
                        pullCollection.Add(pr.Key);
                        commentMessage = abandonMessage;
                    }
                    else
                    {
                        commentMessage = warningMessage;
                    }
                }

                // Add a comment to the PR describing that it's stale
                Comment comment = new Comment { Content = string.Format(commentMessage, pr.Key.Reviewers[0].Id) };
                List<Comment> commentList = new List<Comment> { comment };
                GitPullRequestCommentThread commentThread = new GitPullRequestCommentThread
                {
                    Comments = commentList,
                    Status = CommentThreadStatus.Active
                };
                await gitClient.CreateThreadAsync(commentThread, pr.Key.Repository.Id, pr.Key.PullRequestId);

                // add a tag for this run
                WebApiCreateTagRequestData newLabel = new WebApiCreateTagRequestData
                {
                    Name = string.Format("{0}: (UTC) {1}", warningPrefix, DateTime.UtcNow.ToString("g"))
                };
                await gitClient.CreatePullRequestLabelAsync(newLabel, pr.Key.Repository.Id, pr.Key.PullRequestId);
            }

            return pullCollection;
        }

        public static async Task<bool> AbandonPullRequests(
            GitHttpClient gitClient,
            IList<GitPullRequest> abandonmentCandidates)
        {
            foreach (GitPullRequest pr in abandonmentCandidates)
            {
                try
                {
                    GitPullRequest updatedPr = new GitPullRequest()
                    {
                        Status = PullRequestStatus.Abandoned,
                    };
                    await gitClient.UpdatePullRequestAsync(updatedPr, pr.Repository.Id, pr.PullRequestId);
                }
                catch
                {
                    return false;
                }
            }
            
            return true;
        }

        public static async Task<WorkItemCreateResponse> CreateWorkItem(HttpClient httpClient, string organization, string project, string type, string token, string workItemName)
        {
            var authValue = new AuthenticationHeaderValue("Basic",
                Convert.ToBase64String(
                    System.Text.ASCIIEncoding.ASCII.GetBytes(
                        string.Format("{0}:{1}", "", token))));

            var encodedType = HttpUtility.UrlEncode(type);
            string uri = $"https://dev.azure.com/{organization}/{project}/_apis/wit/workitems/${encodedType}?api-version=6.1-preview.3";
            UserStoryCreateRequest postContent = new UserStoryCreateRequest()
            {
                op = "add",
                path = "/fields/System.Title",
                value = workItemName
            };
            List<UserStoryCreateRequest> postContents = new List<UserStoryCreateRequest>() { postContent };
            var json = Newtonsoft.Json.JsonConvert.SerializeObject(postContents);
            var content = new StringContent(json, Encoding.UTF8, "application/json-patch+json");
            HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Content = content;
            request.Headers.Add("Accept", "application/json-patch+json");
            request.Headers.Authorization = authValue;
            var response = await httpClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                string responseBody = await response.Content.ReadAsStringAsync();
                WorkItemCreateResponse workItem = JsonConvert.DeserializeObject<WorkItemCreateResponse>(responseBody);
                return workItem;
            }
            else
            {
                return null;
            }
        }
    }
}