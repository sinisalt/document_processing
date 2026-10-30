import { z } from "zod";
import type { TFunction } from "i18next";

type Issue = Parameters<z.core.$ZodErrorMap>[0];

function isBlank(input: unknown): boolean {
  return input === undefined || input === null || (typeof input === "string" && input.trim() === "");
}

/**
 * zod's issue → a translated message under `common:validation.*`, for every check that carries no message of its
 * own. A schema-level message (`.min(1, { error: ... })`) still wins: zod consults this map only when the schema
 * gave none, so screens keep their specific texts and nothing falls through to zod's English defaults.
 */
export function zodIssueMessage(issue: Issue, t: TFunction): string {
  const tr = (key: string, values?: Record<string, unknown>) => t(`common:validation.${key}`, values) as string;
  switch (issue.code) {
    case "too_small": {
      const minimum = Number(issue.minimum);
      if (issue.origin === "string") return minimum <= 1 || isBlank(issue.input) ? tr("required") : tr("tooShort", { minimum });
      if (issue.origin === "array" || issue.origin === "set") return tr("tooFewItems", { minimum });
      return tr("tooSmall", { minimum });
    }
    case "too_big": {
      const maximum = Number(issue.maximum);
      if (issue.origin === "string") return tr("tooLong", { maximum });
      if (issue.origin === "array" || issue.origin === "set") return tr("tooManyItems", { maximum });
      return tr("tooBig", { maximum });
    }
    case "invalid_type":
      if (isBlank(issue.input)) return tr("required");
      if (issue.expected === "int") return tr("integer");
      if (issue.expected === "number") return tr("number");
      return tr("invalid");
    case "invalid_format":
      if (issue.format === "email") return tr("email");
      if (issue.format === "url") return tr("url");
      return tr("format");
    case "invalid_value":
      return tr("choice");
    case "not_multiple_of":
      return tr("integer");
    default:
      return tr("invalid");
  }
}

/** Installs {@link zodIssueMessage} as zod's global error map, reading the language at validation time. */
export function installZodErrorMap(t: TFunction): void {
  z.config({ customError: (issue) => zodIssueMessage(issue, t) });
}
