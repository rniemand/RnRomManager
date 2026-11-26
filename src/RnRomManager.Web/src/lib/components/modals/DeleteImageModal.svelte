<script lang="ts">
	import BaseModal from './BaseModal.svelte';
	import type { ImageInfo } from '#lib/types/index.js';

	interface Props {
		open: boolean;
		image: ImageInfo | null;
		gameTitle: string;
		busy?: boolean;
		onconfirm: () => void;
	}

	let { open = $bindable(), image, gameTitle, busy = false, onconfirm }: Props = $props();
</script>

<BaseModal
	bind:open
	title="Delete image"
	confirmLabel="Delete image"
	confirmClass="btn-danger"
	{busy}
	{onconfirm}
>
	<p class="mb-0">
		Delete <code>{image?.fileName}</code> from {gameTitle}? This can't be undone.
	</p>
</BaseModal>
