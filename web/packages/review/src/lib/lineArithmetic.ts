import { toNumber, type Numeric } from "@docproc/app-shell";

/**
 * The validator's money tolerance (`ValidationOptions.MoneyTolerance`, 0.02 in `ValidationContext.cs`; the Validate
 * stage does not override it): differences up to two cents pass, so the grid never reports what LINE_ARITHMETIC accepts.
 */
export const DEFAULT_MONEY_TOLERANCE = 0.02;

/** The server compares decimals; here binary floating point leaves noise (76.82 − 76.8 is 0.0200000000000031). */
const FLOAT_NOISE = 1e-9;

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
  /** quantity × unit price less discount, rounded to cents for display; null when a factor is missing. */
  expected: number | null;
  actual: number | null;
  /** `actual − expected`, rounded to cents for display; null when incomplete. */
  difference: number | null;
}

function round2(value: number): number {
  return Math.round((value + Number.EPSILON) * 100) / 100;
}

/**
 * Mirrors `LineArithmeticRule`: expected = qty × unit price, minus a discount amount (if present) or a discount percent;
 * the line passes when |expected − actual| ≤ tolerance, or when their magnitudes agree (credit notes carry the sign
 * elsewhere). Like the rule, it compares the unrounded expected value. A line without quantity, unit price or line net
 * is "incomplete", never a mismatch.
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
  const ok = Math.abs(actual - expected) <= tolerance + FLOAT_NOISE || Math.abs(Math.abs(expected) - Math.abs(actual)) <= tolerance + FLOAT_NOISE;
  return { status: ok ? "ok" : "mismatch", expected: round2(expected), actual, difference: round2(actual - expected) };
}
