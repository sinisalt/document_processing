/**
 * The codes of `DocumentProcessingFailure` (src/DocProc.Application/Abstractions/DocumentProcessingException.cs). The
 * pipeline stores a document's failure reason as `"<Code>: <message>"` (FailurePolicy), so the console shows the
 * code's translated label instead of the raw enum name; the message after it is the processor's own English text.
 */
export const failureCodes = ["EncryptedPdf", "CorruptPdf", "UnreadablePage", "UnsupportedPlatform", "TooLarge"] as const;

export type FailureCode = (typeof failureCodes)[number];

export interface ParsedFailure {
  /** The `DocumentProcessingFailure` the reason starts with, or null for any other reason (an LLM error, a bug). */
  code: FailureCode | null;
  /** The rest of the reason after `Code: `, or the whole reason when there is no known code. */
  message: string;
}

const PREFIX = /^([A-Za-z]+):\s*([\s\S]*)$/;

export function parseFailureReason(reason: string): ParsedFailure {
  const match = PREFIX.exec(reason.trim());
  if (match && (failureCodes as readonly string[]).includes(match[1]!)) {
    return { code: match[1] as FailureCode, message: match[2]!.trim() };
  }
  return { code: null, message: reason.trim() };
}

/**
 * The reason as one line in the UI language: `Unreadable PDF: The file is not a readable PDF: …` for a known code
 * (`label` translates it), the reason unchanged otherwise.
 */
export function describeFailure(reason: string, label: (code: FailureCode) => string): string {
  const { code, message } = parseFailureReason(reason);
  if (!code) return message;
  return message ? `${label(code)}: ${message}` : label(code);
}
