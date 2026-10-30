import { toNumber, type Numeric } from "@docproc/app-shell";

/** The validator's default money tolerance (docs/02 §"validation"): differences up to one cent pass. */
export const DEFAULT_MONEY_TOLERANCE = 0.01;

export interface LineArithmeticInput {
  quantity: Numeric;
  unitPrice: Numeric;
  lineNet: Numeric;
  discountPercent?: Numeric;
  discountAmount?: Numeric;
}

export type LineArithmeticStatus = "ok" | "mismatch" | "incomplete";

export interface LineArithmeticResult {
  status: LineArithmeticStatus;
  /** quantity × unit price less discount, rounded to cents; null when a factor is missing. */
  expected: number | null;
  actual: number | null;
  /** `actual − expected`, null when incomplete. */
  difference: number | null;
}

function round2(value: number): number {
  return Math.round((value + Number.EPSILON) * 100) / 100;
}

/**
 * Mirrors `LineArithmeticRule`: expected = qty × unit price, minus a discount amount (if present) or a discount percent;
 * the line passes when |expected − actual| ≤ tolerance, or when their magnitudes agree (credit notes carry the sign
 * elsewhere). A line without quantity, unit price or line net is "incomplete", never a mismatch.
 */
export function checkLineArithmetic(line: LineArithmeticInput, tolerance = DEFAULT_MONEY_TOLERANCE): LineArithmeticResult {
  const quantity = toNumber(line.quantity);
  const unitPrice = toNumber(line.unitPrice);
  const actual = toNumber(line.lineNet);
  if (quantity === null || unitPrice === null || actual === null) {
    return { status: "incomplete", expected: null, actual, difference: null };
  }
  let expected = quantity * unitPrice;
  const discountAmount = toNumber(line.discountAmount);
  const discountPercent = toNumber(line.discountPercent);
  if (discountAmount !== null) expected -= Math.abs(discountAmount);
  else if (discountPercent !== null) expected *= 1 - discountPercent / 100;
  expected = round2(expected);
  const difference = round2(actual - expected);
  const ok = Math.abs(difference) <= tolerance || Math.abs(Math.abs(expected) - Math.abs(actual)) <= tolerance;
  return { status: ok ? "ok" : "mismatch", expected, actual, difference };
}
