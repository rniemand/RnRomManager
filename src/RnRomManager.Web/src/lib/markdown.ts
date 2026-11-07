import createDOMPurify from 'dompurify';
import { Marked } from 'marked';

/** B2: a release note's size cap in UTF-8 bytes, matching `LibraryZip.MaxNoteBytes`. */
export const maxNoteBytes = 65536;

/** B5: notes are stored with `\n` line endings. */
export function normaliseNote(text: string): string {
	return text.replace(/\r\n?/g, '\n');
}

/** B11: the byte counter's value — UTF-8 bytes after line-ending normalisation, as the server counts them. */
export function noteBytes(text: string): number {
	return new TextEncoder().encode(normaliseNote(text)).length;
}

// GitHub-flavoured Markdown, no raw-HTML trust: everything goes through DOMPurify.
const marked = new Marked({ gfm: true, breaks: false, async: false });

// Own instance so the link hook doesn't leak into any other DOMPurify user.
const purify = createDOMPurify(window);
purify.addHook('afterSanitizeAttributes', (node) => {
	if (node.tagName === 'A' && node.hasAttribute('href')) {
		node.setAttribute('target', '_blank');
		node.setAttribute('rel', 'noopener noreferrer');
	}
});

/** B11: Markdown → sanitised HTML for `{@html}`; links open in a new tab without an opener. */
export function renderMarkdown(text: string): string {
	const html = marked.parse(text) as string;
	return purify.sanitize(html, { ADD_ATTR: ['target'] });
}
