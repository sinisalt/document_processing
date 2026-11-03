import { readdirSync, readFileSync, statSync } from "node:fs";
import { join } from "node:path";
import { describe, expect, it } from "vitest";
import en from "./en/review.json";
import de from "./de/review.json";
import { fieldLabelKey } from "../lib/labels";
import { compositeMembers, invoiceKinds, isFormSpec, paymentMeans, unitsOfMeasure, type FormSpec, type FormSpecField } from "../lib/formSpec";
import { allSpecs } from "../test/fixtures";

type Tree = { [key: string]: string | Tree };

function leaves(tree: Tree, prefix = ""): string[] {
  return Object.entries(tree).flatMap(([key, value]) => (typeof value === "string" ? [`${prefix}${key}`] : leaves(value, `${prefix}${key}.`)));
}

function has(keys: Set<string>, key: string): boolean {
  return keys.has(key) || keys.has(`${key}_one`) || keys.has(`${key}_other`);
}

function sourceFiles(dir: string): string[] {
  return readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    if (statSync(path).isDirectory()) return sourceFiles(path);
    return /\.(ts|tsx)$/.test(name) && !/\.test\.tsx?$/.test(name) ? [path] : [];
  });
}

const enKeys = new Set(leaves(en as Tree));
const deKeys = new Set(leaves(de as Tree));
const repositoryRoot = join(__dirname, "..", "..", "..", "..", "..");

describe("review i18n resources", () => {
  it("has the same keys in en and de", () => {
    expect([...enKeys].filter((key) => !deKeys.has(key))).toEqual([]);
    expect([...deKeys].filter((key) => !enKeys.has(key))).toEqual([]);
  });

  it("defines every literal key the components use", () => {
    const keys = new Set<string>();
    for (const file of sourceFiles(join(__dirname, ".."))) {
      const text = readFileSync(file, "utf8").replace(/\/\*[\s\S]*?\*\//g, "").replace(/^\s*\/\/.*$/gm, "");
      for (const match of text.matchAll(/\bt\(\s*"([^"]+)"/g)) if (!match[1]!.includes(":")) keys.add(match[1]!);
    }
    expect([...keys].filter((key) => !has(enKeys, key))).toEqual([]);
  });

  it("defines the keys built from enums and member names", () => {
    const expected = [
      ...[...invoiceKinds, ...paymentMeans, ...unitsOfMeasure].map((value) => `enums.${value}`),
      ...["document", "supplier", "customer", "totals", "payment", "notes"].map((section) => `sections.${section}`),
      ...Object.entries(compositeMembers)
        .filter(([kind]) => !["identifier", "money"].includes(kind))
        .flatMap(([kind, members]) => members.filter((m) => !(kind === "party" && m.key === "address")).map((m) => `members.${kind}.${m.key}`)),
    ];
    expect(expected.filter((key) => !has(enKeys, key))).toEqual([]);
  });

  it("labels every field of the built-in invoice form", () => {
    const field = { value: null, status: "Missing" as const };
    const canonical = { orderReferences: [field], deliveryNoteReferences: [field], lines: [{ lineNumber: 1 }] };
    const paths = [...allSpecs(canonical).map((spec) => spec.path), "supplier", "customer", "paymentTerms", "lines[0]", "lines[0].lineNumber"];
    expect(paths.filter((path) => !enKeys.has(fieldLabelKey(path).key))).toEqual([]);
  });

  it("finds an English and a German label for every field, option and section of the shipped templates", () => {
    const dir = join(repositoryRoot, "schemas", "templates");
    const missing: string[] = [];
    const visit = (key: string, fields: readonly FormSpecField[]) => {
      for (const f of fields) {
        if (f.provenance !== false && !(f.label?.en && f.label?.de)) missing.push(`${key}:${f.path}`);
        for (const option of f.options ?? []) if (!(option.label?.en && option.label?.de)) missing.push(`${key}:${f.path}=${option.code}`);
        if (f.children) visit(key, f.children);
      }
    };
    const files = readdirSync(dir).filter((name) => name.endsWith(".json"));
    expect(files.length).toBeGreaterThanOrEqual(9);
    for (const name of files) {
      const spec = (JSON.parse(readFileSync(join(dir, name), "utf8")) as { formSpec: FormSpec }).formSpec;
      expect(isFormSpec(spec)).toBe(true);
      for (const section of spec.sections) if (!(section.label?.en && section.label?.de)) missing.push(`${spec.key}:section ${section.id}`);
      visit(spec.key, spec.fields);
    }
    expect(missing).toEqual([]);
  });
});
