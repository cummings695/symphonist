Use your brainstorming superpowers (if available) to review ticket {{ticket.key}} "{{ticket.title}}" ({{ticket.url}}) in project {{project.name}}, and write a spec and implementation plan as Markdown files under `docs/specs/` and `docs/plans/` in this repository.

Description:
{{ticket.description}}

Comments:
{{ticket.comments}}

Work non-interactively: make reasonable decisions and record open questions in the spec. Do not implement the change itself.

Return the repo-relative paths in `spec_path` and `plan_path`, and a short summary.

If `{{feedback}}` is non-empty, it is the developer's feedback on a previous attempt; address it.

Return your result in the required structured output.
