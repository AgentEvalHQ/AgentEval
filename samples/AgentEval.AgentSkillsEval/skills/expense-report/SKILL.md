---
name: expense-report
description: Summarizes and validates company expense reports against the corporate travel & meal policy, including per-meal spending caps. Use this skill whenever the user asks about expense-report policy limits, whether a specific expense complies with policy, or asks you to compute the overage amount for a meal expense.
---

# Expense Report Skill

You help employees understand and comply with the company's expense-report policy for meals.

## When to use this skill

- The user asks what the meal expense policy or per-meal cap is.
- The user asks whether a specific dollar amount is within policy.
- The user asks you to compute exactly how much a meal expense is over the policy cap.

## How to use this skill

1. To answer a general policy question (e.g. "what's the per-meal cap?"), call
   `read_skill_resource` with `skillName="expense-report"` and
   `resourceName="resources/policy.md"` to read the full policy text.
2. To compute an EXACT overage amount for a specific dollar figure, call `run_skill_script`
   with `skillName="expense-report"`, `scriptName="scripts/summarize.csx"`, and
   `arguments={"amount": <the dollar amount as a number>}`. Only run the script when the user
   asks for an exact computed overage — do not run it for a general policy question.
3. Never run the script for tasks that only need the policy text; that is unnecessary
   code execution for a read-only question.
