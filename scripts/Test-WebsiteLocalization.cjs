"use strict";

const fs = require("node:fs");
const path = require("node:path");
const assert = require("node:assert/strict");
const localization = require("../website/i18n.js");

const repositoryRoot = path.resolve(__dirname, "..");
const htmlPaths = [
  path.join(repositoryRoot, "website", "index.html"),
  path.join(repositoryRoot, "website", "docs", "index.html"),
];
const scriptPaths = [
  path.join(repositoryRoot, "website", "docs", "docs.js"),
];
const allowedChinese = new Set(["简体中文"]);
const chinesePattern = /[\u3400-\u9fff]/u;
const missing = new Set();

function requireTranslation(value, source) {
  const trimmed = value.replace(/\s+/gu, " ").trim();
  if (!trimmed || !chinesePattern.test(trimmed) || allowedChinese.has(trimmed)) return;
  if (/^\d{4}\.\d{2}\.\d{2} 更新$/u.test(trimmed)) return;
  if (!localization.english[trimmed]) missing.add(`${source}: ${trimmed}`);
}

for (const htmlPath of htmlPaths) {
  const html = fs.readFileSync(htmlPath, "utf8");
  for (const match of html.matchAll(/>([^<>]+)</gu)) {
    requireTranslation(match[1], path.relative(repositoryRoot, htmlPath));
  }
  for (const match of html.matchAll(/(?:aria-label|title|placeholder|content)="([^"]+)"/gu)) {
    requireTranslation(match[1], path.relative(repositoryRoot, htmlPath));
  }
}

for (const scriptPath of scriptPaths) {
  const script = fs.readFileSync(scriptPath, "utf8");
  for (const match of script.matchAll(/"([^"\r\n]*[\u3400-\u9fff][^"\r\n]*)"/gu)) {
    requireTranslation(match[1], path.relative(repositoryRoot, scriptPath));
  }
}

assert.deepEqual([...missing], [], `Missing English website translations:\n${[...missing].join("\n")}`);
assert.equal(localization.chooseLanguage(["zh-CN", "en-US"]), "zh-Hans");
assert.equal(localization.chooseLanguage(["en-GB", "zh-CN"]), "en");
assert.equal(localization.chooseLanguage(["fr-FR", "en-US"]), "en");
assert.equal(localization.chooseLanguage(["fr-FR"]), "en");

const landing = fs.readFileSync(htmlPaths[0], "utf8");
const docs = fs.readFileSync(htmlPaths[1], "utf8");
assert.ok(landing.indexOf("i18n.js") < landing.indexOf("app.js"));
assert.ok(docs.indexOf("../i18n.js") < docs.indexOf("docs.js"));
assert.match(landing, /id="gitcode-download"[^>]+-setup\.exe\/download/);
assert.match(landing, /id="github-download"[^>]+-setup\.exe/);
assert.match(landing, /id="gitcode-portable"[^>]+-win-x64\.zip\/download/);
assert.match(landing, /id="github-portable"[^>]+-win-x64\.zip/);

console.log(`WEBSITE_LOCALIZATION_OK=${Object.keys(localization.english).length}`);
