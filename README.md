# Issue Label Watcher

This project is a DIY workaround for GitHub not supporting you to subscribe to a label for notifications when new issues are assigned with it.
It works by polling GitHub through the GraphQL API using a GitHub Personal Access Token for all issues with the label, and sending them in an email to you.

It is intended to be installed as an Azure Web Job in an Azure App Service, although it can be run as a normal console app under .NET Core 3.1.
You need to be a fairly technical person, ideally with an existing Azure App Service, in order to use Issue Label Watcher.
This is not a general solution that everyone can use.

## Goals

Issue Label Watcher is the solution to the following problem:

1. You want an email when an issue is assigned to a specific label in a specific repository, but not get notified multiple times for the same issue. If you want to follow the issue, you'll manually subscribe to the issue on GitHub.
1. The target repository is highly active, so subscribing to all issues in the repository would be overwhelming.
1. You have no access to the target repository. (Installing webhooks, actions, apps, etc. would enable a much more elegant push-based solution).
1. You want to be able to watch multiple repositories, following different labels for each.

## Usage

If you are interested in the `alabel` label in the `user/repo1` repository, and the `blabel` and `clabel` labels in the `org/repo2` repository then add the following Application Settings in the App Service:

```
"ilw:Repos": "user/repo1;org/repo2",
"ilw:Repo:user/repo1:Labels": "alabel",
"ilw:Repo:org/repo2:Labels": "blabel;clabel"
```

The following Application Settings are also required.
Obviously, the values are examples and should be replaced with your own.
The only permission required for the PAT is `public_repo`.
It matters which GitHub user created the PAT because IssueLabelWatcher tries not to send an email for issues that the user has already manually subscribed to.

```
"ilw:GithubPersonalAccessToken": "abcdef0123456789abcdef0123456789abcdef01",
"ilw:SmtpServer": "smtp.gmail.com",
"ilw:SmtpPort": "587",
"ilw:SmtpFrom": "@gmail.com",
"ilw:SmtpTo": "@gmail.com",
"ilw:SmtpUsername": "@gmail.com",
"ilw:SmtpPassword": "password"
```

Also, add the following Connection String (a Storage Account is required to remember which issues have already been sent to you):

```
"AzureWebJobsStorage": "DefaultEndpointsProtocol=https;AccountName={name};AccountKey={key};EndpointSuffix=core.windows.net",
```
