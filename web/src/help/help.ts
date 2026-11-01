import { Marked } from 'marked';
import changelog from './changelog.md?raw';
import guide from './guide.md?raw';

export { changelog, guide };

export interface Heading { id: string; text: string; depth: number }

const slug = (text: string) => text.toLowerCase().replace(/<[^>]+>/g, '').replace(/[^\p{L}\p{N}]+/gu, '-').replace(/^-|-$/g, '');

/** Headings (h2, h3) for a table of contents; ids match the rendered HTML. */
export function headings(md: string): Heading[] {
  return [...md.matchAll(/^(#{2,3}) (.+)$/gm)].map((m) => {
    const text = m[2].replace(/\*\*/g, '');
    return { depth: m[1].length, text, id: slug(text) };
  });
}

const parser = new Marked({
  gfm: true,
  renderer: {
    heading({ tokens, depth }) {
      const text = this.parser.parseInline(tokens);
      return `<h${depth} id="${slug(text)}">${text}</h${depth}>\n`;
    },
    link({ href, tokens }) {
      const text = this.parser.parseInline(tokens);
      return `<a href="${href}"${/^https?:/.test(href) ? ' target="_blank" rel="noreferrer"' : ''}>${text}</a>`;
    },
  },
});

/** The help texts are our own files in the repository (web/src/help), not user input. */
export function render(md: string): string {
  return parser.parse(md, { async: false }) as string;
}
