import { test } from "node:test";
import assert from "node:assert/strict";
import { parseXml, findChild, findChildren, stripNamespace, isElement } from "../dist/xml.js";

test("parses a minimal self-closing element", () => {
    const root = parseXml(`<Project/>`);
    assert.equal(stripNamespace(root.name), "Project");
    assert.deepEqual(root.attributes, {});
    assert.equal(root.children.length, 0);
});

test("parses attributes with both quote styles and entity decode", () => {
    const root = parseXml(`<Project Sdk="Microsoft.NET.Sdk" Label='A &amp; B'/>`);
    assert.equal(root.attributes.Sdk, "Microsoft.NET.Sdk");
    assert.equal(root.attributes.Label, "A & B");
});

test("parses nested elements and concatenates text", () => {
    const root = parseXml(`<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>`);
    const pg = findChild(root, "PropertyGroup");
    assert.ok(pg);
    const tfm = findChild(pg, "TargetFramework");
    assert.ok(tfm);
    assert.equal(tfm.text, "net10.0");
});

test("skips comments, PIs, and leading BOM", () => {
    const xml = "\uFEFF<?xml version=\"1.0\"?>\n<!-- top-level comment -->\n<Project><!-- inner --><X/></Project>";
    const root = parseXml(xml);
    assert.equal(root.name, "Project");
    assert.equal(findChildren(root, "X").length, 1);
});

test("findChild returns undefined when missing", () => {
    const root = parseXml(`<Project><A/></Project>`);
    assert.equal(findChild(root, "B"), undefined);
});

test("isElement narrows out text nodes", () => {
    const root = parseXml(`<A>hello<B/></A>`);
    const nonText = root.children.filter(isElement);
    assert.equal(nonText.length, 1);
    assert.equal(nonText[0].name, "B");
});

test("throws on mismatched close tag", () => {
    assert.throws(() => parseXml(`<A><B></C></A>`), /Mismatched close tag/);
});
