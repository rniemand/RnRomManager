<script lang="ts">
	import type { Snippet } from 'svelte';
	import Icon from '#lib/icons/Icon.svelte';

	interface Props {
		disabled?: boolean;
		/** Which dropped files are accepted; all of them when omitted. */
		accept?: (file: File) => boolean;
		/** The accepted files, when there is at least one. */
		ondrop: (files: File[]) => void;
		/** The rejected files, when there is at least one. */
		onreject?: (files: File[]) => void;
		hint: string;
		class?: string;
		children: Snippet;
	}

	let {
		disabled = false,
		accept,
		ondrop,
		onreject,
		hint,
		class: className,
		children
	}: Props = $props();

	let dragging = $state(false);

	const hasFiles = (e: DragEvent) => !!e.dataTransfer?.types.includes('Files');

	function onDragOver(e: DragEvent): void {
		if (disabled || !hasFiles(e)) return;
		e.preventDefault(); // allow the drop, so the browser never navigates to the file
		dragging = true;
	}

	function onDragLeave(e: DragEvent): void {
		const target = e.currentTarget as HTMLElement;
		if (!target.contains(e.relatedTarget as Node | null)) dragging = false;
	}

	function onDrop(e: DragEvent): void {
		if (disabled || !hasFiles(e)) return;
		e.preventDefault();
		dragging = false;
		const files = [...e.dataTransfer!.files];
		const accepted = accept ? files.filter(accept) : files;
		const rejected = accept ? files.filter((f) => !accept(f)) : [];
		if (accepted.length) ondrop(accepted);
		if (rejected.length) onreject?.(rejected);
	}
</script>

<!-- Z1: a mouse convenience; every drop zone has a keyboard/button path beside it. -->
<div class={className}>
	<div
		role="presentation"
		class={['rounded', dragging && 'border border-2 border-primary border-dashed bg-primary-subtle']
			.filter(Boolean)
			.join(' ')}
		ondragenter={onDragOver}
		ondragover={onDragOver}
		ondragleave={onDragLeave}
		ondrop={onDrop}
	>
		{@render children()}
	</div>
	<div class="small text-body-secondary mt-1"><Icon name="upload" /> {hint}</div>
</div>
