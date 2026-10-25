namespace Symphonist.Integrations.GitHub;

/// <summary>
/// GraphQL text for the Projects (v2) tracker, checked against GitHub's public schema (ProjectV2Owner.projectV2,
/// ProjectV2.field/items, ProjectV2SingleSelectField.options, ProjectV2ItemFieldSingleSelectValue.name,
/// Issue.comments). Each query has an operation name so requests are easy to tell apart in logs and tests.
/// </summary>
internal static class GitHubProjectsQueries
{
    public const string Viewer = "query Viewer { viewer { login } }";

    /// <summary>Variables: <c>owner</c> (String!), <c>number</c> (Int!), <c>field</c> (String!).</summary>
    public const string Project = """
        query Project($owner: String!, $number: Int!, $field: String!) {
          repositoryOwner(login: $owner) {
            ... on ProjectV2Owner {
              projectV2(number: $number) {
                id
                field(name: $field) {
                  ... on ProjectV2SingleSelectField { id options { id name } }
                }
              }
            }
          }
        }
        """;

    /// <summary>Variables: <c>project</c> (ID!), <c>field</c> (String!), <c>after</c> (String, null for page one).</summary>
    public const string Items = """
        query Items($project: ID!, $field: String!, $after: String) {
          node(id: $project) {
            ... on ProjectV2 {
              items(first: 100, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes {
                  id
                  fieldValueByName(name: $field) {
                    ... on ProjectV2ItemFieldSingleSelectValue { name }
                  }
                  content {
                    __typename
                    ... on Issue {
                      id number title body url state
                      repository { name nameWithOwner }
                      assignees(first: 20) { nodes { login } }
                      labels(first: 50) { nodes { name } }
                    }
                  }
                }
              }
            }
          }
        }
        """;

    /// <summary>Variables: <c>issue</c> (ID!), <c>after</c> (String, null for page one).</summary>
    public const string Comments = """
        query Comments($issue: ID!, $after: String) {
          node(id: $issue) {
            ... on Issue {
              comments(first: 100, after: $after) {
                pageInfo { hasNextPage endCursor }
                nodes { author { login } body createdAt }
              }
            }
          }
        }
        """;

    /// <summary>Variables: <c>project</c>, <c>item</c>, <c>field</c> (all ID!) and <c>option</c> (String!, the option id).</summary>
    public const string SetStatus = """
        mutation SetStatus($project: ID!, $item: ID!, $field: ID!, $option: String!) {
          updateProjectV2ItemFieldValue(input: {
            projectId: $project, itemId: $item, fieldId: $field, value: { singleSelectOptionId: $option }
          }) { clientMutationId }
        }
        """;

    /// <summary>Variables: <c>subject</c> (ID!, the issue node id), <c>body</c> (String!, Markdown).</summary>
    public const string AddComment = """
        mutation AddComment($subject: ID!, $body: String!) {
          addComment(input: { subjectId: $subject, body: $body }) { clientMutationId }
        }
        """;
}
