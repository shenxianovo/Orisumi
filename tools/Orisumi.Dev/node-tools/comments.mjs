import fs from "node:fs";
import path from "node:path";
import ts from "typescript";

const root = path.resolve(process.argv[2]);
const files = JSON.parse(fs.readFileSync(process.argv[3], "utf8"));

function scriptKind(file) {
  if (/\.tsx$/.test(file)) return ts.ScriptKind.TSX;
  if (/\.jsx$/.test(file)) return ts.ScriptKind.JSX;
  if (/\.[cm]?js$/.test(file)) return ts.ScriptKind.JS;
  return ts.ScriptKind.TS;
}

function commentsOf(relative) {
  const text = fs.readFileSync(path.join(root, relative), "utf8");
  const source = ts.createSourceFile(relative, text, ts.ScriptTarget.Latest, true, scriptKind(relative));
  const ranges = new Map();
  const collect = (position, scan) => scan(text, position, (pos, end) => ranges.set(pos, end));
  const visit = (node) => {
    if (node.kind === ts.SyntaxKind.JsxText) return;
    collect(node.pos, ts.forEachLeadingCommentRange);
    collect(node.end, ts.forEachTrailingCommentRange);
    for (const child of node.getChildren(source)) visit(child);
  };
  visit(source);
  return [...ranges].map(([pos, end]) => ({
    path: relative,
    line: source.getLineAndCharacterOfPosition(pos).line + 1,
    text: text.slice(pos, end),
  }));
}

process.stdout.write(JSON.stringify(files.flatMap(commentsOf)));
