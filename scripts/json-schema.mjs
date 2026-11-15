// A small JSON Schema (draft 2020-12) checker for this repo's contracts: exactly the keywords the schemas in
// contracts/ use, so the engine's tests (engine/tests/Knowledge.Tests/Schemas.cs) can implement the same subset.
// An unknown keyword is an error, which keeps the schemas inside that subset. No dependencies.
//
//   import { check } from "./json-schema.mjs";
//   check("contracts/mcp", "app_graph.output.schema.json", value)   // [] when valid, else "<pointer>: <problem>" lines
//
// $ref is "#/<pointer>" within the file or "<file>#/<pointer>" for a file in the same folder. format is an
// annotation only (the schemas say the same thing with a pattern). A pattern starting with (?i) is case-insensitive.
// unevaluatedProperties checks each member that no properties or additionalProperties evaluated, here or in what
// $ref, allOf and the passing anyOf and oneOf branches reach (the settings schema, contracts/rhythm.schema.json, gives
// each adapter section the shared adapter definition and its own keys this way).
import { readFileSync } from "node:fs";
import { join } from "node:path";

const annotations = new Set(["$schema", "$id", "title", "description", "$comment", "default", "examples", "$defs", "format"]);
const assertions = new Set([
  "$ref", "type", "enum", "const", "minimum", "maximum", "exclusiveMinimum", "exclusiveMaximum", "minLength", "maxLength",
  "pattern", "minItems", "maxItems", "uniqueItems", "prefixItems", "items", "required", "properties", "additionalProperties",
  "propertyNames", "minProperties", "maxProperties", "allOf", "anyOf", "oneOf", "not", "unevaluatedProperties",
]);
const isObject = (v) => v !== null && typeof v === "object" && !Array.isArray(v);
const is = {
  object: isObject,
  array: Array.isArray,
  string: (v) => typeof v === "string",
  number: (v) => typeof v === "number" && Number.isFinite(v),
  integer: (v) => Number.isInteger(v),
  boolean: (v) => typeof v === "boolean",
  null: (v) => v === null,
};
const equal = (a, b) => {
  if (Array.isArray(a)) return Array.isArray(b) && a.length === b.length && a.every((x, i) => equal(x, b[i]));
  if (isObject(a)) {
    const keys = Object.keys(a);
    return isObject(b) && keys.length === Object.keys(b).length && keys.every((k) => k in b && equal(a[k], b[k]));
  }
  return a === b;
};
const regex = (pattern) => (pattern.startsWith("(?i)") ? new RegExp(pattern.slice(4), "iu") : new RegExp(pattern, "u"));

// Every keyword anywhere in a schema file is one this checker knows.
export function unsupported(schema, at = "") {
  if (typeof schema === "boolean") return [];
  if (!isObject(schema)) return [`${at || "/"}: a schema is an object or a boolean`];
  const found = Object.keys(schema).filter((k) => !annotations.has(k) && !assertions.has(k)).map((k) => `${at}/${k}: unsupported keyword`);
  const nested = (s, p) => found.push(...unsupported(s, p));
  for (const k of ["items", "additionalProperties", "propertyNames", "not", "unevaluatedProperties"]) if (k in schema) nested(schema[k], `${at}/${k}`);
  for (const k of ["prefixItems", "allOf", "anyOf", "oneOf"]) (schema[k] ?? []).forEach((s, i) => nested(s, `${at}/${k}/${i}`));
  for (const k of ["properties", "$defs"]) for (const [name, s] of Object.entries(schema[k] ?? {})) nested(s, `${at}/${k}/${name}`);
  return found;
}

export function check(folder, file, value) {
  const docs = new Map();
  const doc = (name) => {
    if (!docs.has(name)) docs.set(name, JSON.parse(readFileSync(join(folder, name), "utf8")));
    return docs.get(name);
  };
  const resolve = (ref, base) => {
    const [name, pointer = ""] = ref.split("#");
    const target = name || base;
    let node = doc(target);
    for (const part of pointer.split("/").slice(1)) node = node?.[part.replaceAll("~1", "/").replaceAll("~0", "~")];
    if (node === undefined) throw new Error(`${base}: $ref ${ref} does not resolve`);
    return [node, target];
  };
  const errors = (schema, v, at, base) => {
    if (schema === true) return [];
    if (schema === false) return [`${at || "/"}: not allowed here`];
    const here = at || "/";
    const out = [];
    const bad = Object.keys(schema).find((k) => !annotations.has(k) && !assertions.has(k));
    if (bad) throw new Error(`${base}: unsupported keyword ${bad}`);
    if (schema.$ref) {
      const [s, b] = resolve(schema.$ref, base);
      out.push(...errors(s, v, at, b));
    }
    if (schema.type !== undefined) {
      const types = [].concat(schema.type);
      if (!types.some((t) => is[t](v))) return [...out, `${here}: expected ${types.join(" or ")}`];
    }
    if ("const" in schema && !equal(schema.const, v)) out.push(`${here}: expected ${JSON.stringify(schema.const)}`);
    if (schema.enum && !schema.enum.some((e) => equal(e, v))) out.push(`${here}: not one of ${JSON.stringify(schema.enum)}`);
    if (typeof v === "number") {
      if (schema.minimum !== undefined && v < schema.minimum) out.push(`${here}: below ${schema.minimum}`);
      if (schema.maximum !== undefined && v > schema.maximum) out.push(`${here}: above ${schema.maximum}`);
      if (schema.exclusiveMinimum !== undefined && v <= schema.exclusiveMinimum) out.push(`${here}: not above ${schema.exclusiveMinimum}`);
      if (schema.exclusiveMaximum !== undefined && v >= schema.exclusiveMaximum) out.push(`${here}: not below ${schema.exclusiveMaximum}`);
    }
    if (typeof v === "string") {
      const length = [...v].length;
      if (schema.minLength !== undefined && length < schema.minLength) out.push(`${here}: shorter than ${schema.minLength}`);
      if (schema.maxLength !== undefined && length > schema.maxLength) out.push(`${here}: longer than ${schema.maxLength}`);
      if (schema.pattern !== undefined && !regex(schema.pattern).test(v)) out.push(`${here}: does not match ${schema.pattern}`);
    }
    if (Array.isArray(v)) {
      if (schema.minItems !== undefined && v.length < schema.minItems) out.push(`${here}: fewer than ${schema.minItems} items`);
      if (schema.maxItems !== undefined && v.length > schema.maxItems) out.push(`${here}: more than ${schema.maxItems} items`);
      if (schema.uniqueItems && v.some((x, i) => v.slice(0, i).some((y) => equal(x, y)))) out.push(`${here}: items repeat`);
      const prefix = schema.prefixItems ?? [];
      v.forEach((x, i) => {
        const s = i < prefix.length ? prefix[i] : schema.items;
        if (s !== undefined) out.push(...errors(s, x, `${at}/${i}`, base));
      });
    }
    if (isObject(v)) {
      const keys = Object.keys(v);
      for (const name of schema.required ?? []) if (!(name in v)) out.push(`${here}: ${name} is required`);
      if (schema.minProperties !== undefined && keys.length < schema.minProperties) out.push(`${here}: fewer than ${schema.minProperties} members`);
      if (schema.maxProperties !== undefined && keys.length > schema.maxProperties) out.push(`${here}: more than ${schema.maxProperties} members`);
      for (const k of keys) {
        const p = `${at}/${k.replaceAll("~", "~0").replaceAll("/", "~1")}`;
        if (schema.propertyNames !== undefined) out.push(...errors(schema.propertyNames, k, `${p} (name)`, base));
        if (schema.properties && k in schema.properties) out.push(...errors(schema.properties[k], v[k], p, base));
        else if (schema.additionalProperties !== undefined) out.push(...errors(schema.additionalProperties, v[k], p, base));
      }
    }
    for (const s of schema.allOf ?? []) out.push(...errors(s, v, at, base));
    if (schema.anyOf && !schema.anyOf.some((s) => errors(s, v, at, base).length === 0)) out.push(`${here}: matches none of anyOf`);
    if (schema.oneOf) {
      const passing = schema.oneOf.filter((s) => errors(s, v, at, base).length === 0).length;
      if (passing !== 1) out.push(`${here}: matches ${passing} of oneOf, not exactly one`);
    }
    if (schema.not !== undefined && errors(schema.not, v, at, base).length === 0) out.push(`${here}: matches what is not allowed`);
    if (schema.unevaluatedProperties !== undefined && isObject(v)) {
      const seen = evaluated(schema, v, at, base, true);
      for (const k of Object.keys(v))
        if (!seen.has(k)) out.push(...errors(schema.unevaluatedProperties, v[k], `${at}/${k.replaceAll("~", "~0").replaceAll("/", "~1")}`, base));
    }
    return out;
  };
  // The members of an object that a schema evaluates: its properties, every member once it has additionalProperties
  // (or, below the schema asking, unevaluatedProperties), and what $ref, allOf and the passing anyOf and oneOf reach.
  const evaluated = (schema, v, at, base, asking = false, seen = new Set()) => {
    if (!isObject(schema)) return seen;
    for (const k of Object.keys(schema.properties ?? {})) if (k in v) seen.add(k);
    if (schema.additionalProperties !== undefined || (!asking && schema.unevaluatedProperties !== undefined))
      for (const k of Object.keys(v)) seen.add(k);
    if (schema.$ref) {
      const [s, b] = resolve(schema.$ref, base);
      evaluated(s, v, at, b, false, seen);
    }
    for (const s of schema.allOf ?? []) evaluated(s, v, at, base, false, seen);
    for (const s of [...(schema.anyOf ?? []), ...(schema.oneOf ?? [])])
      if (errors(s, v, at, base).length === 0) evaluated(s, v, at, base, false, seen);
    return seen;
  };
  return errors(doc(file), value, "", file);
}
