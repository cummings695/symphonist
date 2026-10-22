Implement ticket {{ticket.key}} "{{ticket.title}}" ({{ticket.url}}) in project {{project.name}}, following the spec at {{spec_path}} and the plan at {{plan_path}}, using multiple agents if available.

Work on branch {{branch}}. Write tests, run them, and check your work. Do not commit or push; leave changes in the working tree.

Return `pr_title`, `pr_description`, `tests_passed`, and a short summary.

If `{{feedback}}` is non-empty, it is the developer's feedback on a previous attempt; address it.

Return your result in the required structured output.
