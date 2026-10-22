You are triaging a ticket for project {{project.name}}.

Read ticket {{ticket.key}} "{{ticket.title}}" ({{ticket.url}}), its description and comments, and the code in this repository.

Description:
{{ticket.description}}

Comments:
{{ticket.comments}}

Decide whether the ticket is clear and feasible, and propose the best path forward. Do not change any files.

Put a concise comment for the ticket (approach, affected areas, open questions) in `comment_markdown`.

If `{{feedback}}` is non-empty, it is the developer's feedback on a previous attempt; address it.

Return your result in the required structured output.
