// Minimal XML parser for .csproj files. Supports elements, attributes, text, CDATA,
// comments, and processing instructions. Does NOT support namespaces beyond stripping them
// on read (same as cs_kit's _strip_ns). No external dependency — see M5 D57.

export interface XmlElement {
    name: string;
    attributes: Record<string, string>;
    children: XmlNode[];
    /** Concatenated direct text nodes, stripped. Empty string if none. */
    text: string;
}

export type XmlNode = XmlElement | XmlText;

export interface XmlText {
    kind: "text";
    value: string;
}

export function isText(node: XmlNode): node is XmlText {
    return (node as XmlText).kind === "text";
}

export function isElement(node: XmlNode): node is XmlElement {
    return (node as XmlText).kind !== "text";
}

export class XmlParseError extends Error {
    constructor(message: string, public readonly offset: number) {
        super(`${message} at offset ${offset}`);
        this.name = "XmlParseError";
    }
}

/** Strip an optional BOM and leading whitespace. Returns a parsed root element. */
export function parseXml(source: string): XmlElement {
    const text = source.charCodeAt(0) === 0xfeff ? source.slice(1) : source;
    const parser = new Parser(text);
    const root = parser.parseRoot();
    return root;
}

/** Strip `{namespace}` prefix from an element name (MSBuild namespaces are noise for us). */
export function stripNamespace(name: string): string {
    const brace = name.indexOf("}");
    return brace >= 0 ? name.slice(brace + 1) : name;
}

/** Find direct children whose (namespace-stripped) name matches. */
export function findChildren(element: XmlElement, name: string): XmlElement[] {
    const out: XmlElement[] = [];
    for (const child of element.children) {
        if (isElement(child) && stripNamespace(child.name) === name) {
            out.push(child);
        }
    }
    return out;
}

/** Find the first direct child with the given name, or undefined. */
export function findChild(element: XmlElement, name: string): XmlElement | undefined {
    for (const child of element.children) {
        if (isElement(child) && stripNamespace(child.name) === name) {
            return child;
        }
    }
    return undefined;
}

class Parser {
    private pos = 0;

    constructor(private readonly input: string) {}

    parseRoot(): XmlElement {
        this.skipProlog();
        const el = this.readElement();
        this.skipWhitespaceAndNoise();
        if (this.pos < this.input.length) {
            throw new XmlParseError(`Unexpected trailing content`, this.pos);
        }
        return el;
    }

    private skipProlog(): void {
        this.skipWhitespace();
        // <?xml ... ?>
        while (this.pos < this.input.length) {
            this.skipWhitespace();
            if (this.input.startsWith("<?", this.pos)) {
                const end = this.input.indexOf("?>", this.pos);
                if (end < 0) throw new XmlParseError("Unterminated <?...?>", this.pos);
                this.pos = end + 2;
            } else if (this.input.startsWith("<!--", this.pos)) {
                this.skipComment();
            } else if (this.input.startsWith("<!", this.pos)) {
                // DOCTYPE or similar declaration — skip to matching >.
                const end = this.input.indexOf(">", this.pos);
                if (end < 0) throw new XmlParseError("Unterminated <!...>", this.pos);
                this.pos = end + 1;
            } else {
                return;
            }
        }
    }

    private skipWhitespace(): void {
        while (this.pos < this.input.length) {
            const c = this.input.charCodeAt(this.pos);
            if (c !== 0x20 && c !== 0x09 && c !== 0x0a && c !== 0x0d) return;
            this.pos++;
        }
    }

    private skipComment(): void {
        // <!-- ... -->
        const end = this.input.indexOf("-->", this.pos);
        if (end < 0) throw new XmlParseError("Unterminated <!-- -->", this.pos);
        this.pos = end + 3;
    }

    /** Skip inter-element whitespace, comments, and processing instructions. */
    private skipWhitespaceAndNoise(): void {
        while (this.pos < this.input.length) {
            this.skipWhitespace();
            if (this.input.startsWith("<!--", this.pos)) {
                this.skipComment();
            } else if (this.input.startsWith("<?", this.pos)) {
                const end = this.input.indexOf("?>", this.pos);
                if (end < 0) throw new XmlParseError("Unterminated <?...?>", this.pos);
                this.pos = end + 2;
            } else {
                return;
            }
        }
    }

    private readElement(): XmlElement {
        this.skipWhitespaceAndNoise();
        if (this.input.charCodeAt(this.pos) !== 0x3c) {
            throw new XmlParseError("Expected '<'", this.pos);
        }
        this.pos++; // consume '<'
        const name = this.readName();
        const attributes = this.readAttributes();
        this.skipWhitespace();

        if (this.input.startsWith("/>", this.pos)) {
            this.pos += 2;
            return { name, attributes, children: [], text: "" };
        }
        if (this.input.charCodeAt(this.pos) !== 0x3e) {
            throw new XmlParseError(`Expected '>' in <${name}>`, this.pos);
        }
        this.pos++; // consume '>'

        const children: XmlNode[] = [];
        const textChunks: string[] = [];
        while (this.pos < this.input.length) {
            if (this.input.startsWith("</", this.pos)) {
                this.pos += 2;
                const closeName = this.readName();
                this.skipWhitespace();
                if (this.input.charCodeAt(this.pos) !== 0x3e) {
                    throw new XmlParseError(`Expected '>' for </${closeName}>`, this.pos);
                }
                this.pos++;
                if (closeName !== name) {
                    throw new XmlParseError(`Mismatched close tag </${closeName}> for <${name}>`, this.pos);
                }
                return { name, attributes, children, text: textChunks.join("").trim() };
            }
            if (this.input.startsWith("<!--", this.pos)) {
                this.skipComment();
                continue;
            }
            if (this.input.startsWith("<![CDATA[", this.pos)) {
                const end = this.input.indexOf("]]>", this.pos + 9);
                if (end < 0) throw new XmlParseError("Unterminated <![CDATA[", this.pos);
                const value = this.input.slice(this.pos + 9, end);
                textChunks.push(value);
                children.push({ kind: "text", value });
                this.pos = end + 3;
                continue;
            }
            if (this.input.charCodeAt(this.pos) === 0x3c /* '<' */) {
                const child = this.readElement();
                children.push(child);
                continue;
            }
            // Text node — read up to next '<'.
            const nextLt = this.input.indexOf("<", this.pos);
            if (nextLt < 0) throw new XmlParseError(`Unterminated element <${name}>`, this.pos);
            const raw = this.input.slice(this.pos, nextLt);
            const decoded = decodeEntities(raw);
            textChunks.push(decoded);
            children.push({ kind: "text", value: decoded });
            this.pos = nextLt;
        }
        throw new XmlParseError(`Unterminated element <${name}>`, this.pos);
    }

    private readName(): string {
        const start = this.pos;
        while (this.pos < this.input.length) {
            const c = this.input.charCodeAt(this.pos);
            // Name chars: letters, digits, '-', '_', '.', ':', (some Unicode)
            if (
                (c >= 0x41 && c <= 0x5a) || // A–Z
                (c >= 0x61 && c <= 0x7a) || // a–z
                (c >= 0x30 && c <= 0x39) || // 0–9
                c === 0x2d ||               // -
                c === 0x2e ||               // .
                c === 0x3a ||               // :
                c === 0x5f                  // _
            ) {
                this.pos++;
            } else {
                break;
            }
        }
        if (this.pos === start) {
            throw new XmlParseError("Expected element name", this.pos);
        }
        return this.input.slice(start, this.pos);
    }

    private readAttributes(): Record<string, string> {
        const attrs: Record<string, string> = {};
        while (true) {
            this.skipWhitespace();
            const c = this.input.charCodeAt(this.pos);
            if (c === 0x2f /* / */ || c === 0x3e /* > */) return attrs;
            const name = this.readName();
            this.skipWhitespace();
            if (this.input.charCodeAt(this.pos) !== 0x3d) {
                throw new XmlParseError(`Expected '=' after attribute '${name}'`, this.pos);
            }
            this.pos++;
            this.skipWhitespace();
            const quote = this.input.charCodeAt(this.pos);
            if (quote !== 0x22 /* " */ && quote !== 0x27 /* ' */) {
                throw new XmlParseError(`Expected quote around attribute '${name}' value`, this.pos);
            }
            this.pos++;
            const start = this.pos;
            const end = this.input.indexOf(String.fromCharCode(quote), start);
            if (end < 0) {
                throw new XmlParseError(`Unterminated attribute value for '${name}'`, start);
            }
            attrs[name] = decodeEntities(this.input.slice(start, end));
            this.pos = end + 1;
        }
    }
}

function decodeEntities(s: string): string {
    // MSBuild csproj files use a small set of XML entities; handle them here rather than
    // pulling in a full entity table.
    if (!s.includes("&")) return s;
    return s
        .replace(/&lt;/g, "<")
        .replace(/&gt;/g, ">")
        .replace(/&quot;/g, "\"")
        .replace(/&apos;/g, "'")
        .replace(/&amp;/g, "&");
}
