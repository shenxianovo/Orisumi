import fs from "node:fs";
import path from "node:path";
import ts from "typescript";

const [root, list, mode] = [path.resolve(process.argv[2]), process.argv[3], process.argv[4]];
const files = JSON.parse(fs.readFileSync(list, "utf8"));

const settings = {
  ...ts.getDefaultFormatCodeSettings("\n"),
  indentSize: 2,
  tabSize: 2,
  convertTabsToSpaces: true,
  insertSpaceAfterOpeningAndBeforeClosingNonemptyBraces: true,
  insertSpaceAfterOpeningAndBeforeClosingEmptyBraces: false,
  semicolons: ts.SemicolonPreference.Ignore,
};

const texts = new Map(files.map((file) => [file, fs.readFileSync(path.join(root, file), "utf8")]));
const host = {
  getCompilationSettings: () => ({ allowJs: true, jsx: ts.JsxEmit.Preserve }),
  getScriptFileNames: () => files,
  getScriptVersion: () => "0",
  getScriptSnapshot: (file) => texts.has(file) ? ts.ScriptSnapshot.fromString(texts.get(file)) : undefined,
  getCurrentDirectory: () => root,
  getDefaultLibFileName: (options) => ts.getDefaultLibFilePath(options),
  fileExists: (file) => texts.has(file),
  readFile: (file) => texts.get(file),
};
const service = ts.createLanguageService(host, ts.createDocumentRegistry());

const changed = [];
for (const file of files) {
  const text = texts.get(file);
  const edits = service.getFormattingEditsForDocument(file, settings).sort((a, b) => b.span.start - a.span.start);
  let formatted = text;
  for (const edit of edits) formatted = formatted.slice(0, edit.span.start) + edit.newText + formatted.slice(edit.span.start + edit.span.length);
  if (formatted === text) continue;
  changed.push(file);
  if (mode === "write") fs.writeFileSync(path.join(root, file), formatted);
}
process.stdout.write(JSON.stringify(changed));
