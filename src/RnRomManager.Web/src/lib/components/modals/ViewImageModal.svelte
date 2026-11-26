<script lang="ts">
	import BaseModal from './BaseModal.svelte';
	import { imageTypeLabels } from '#lib/imageResolutions.js';
	import type { ImageInfo } from '#lib/types/index.js';

	interface Props {
		open: boolean;
		image: ImageInfo | null;
		/** The game's title, for the alt text. */
		gameTitle: string;
	}

	let { open = $bindable(), image, gameTitle }: Props = $props();
</script>

<BaseModal bind:open title={image?.fileName ?? 'Image'} size="lg">
	{#if image}
		<img
			class="img-fluid d-block mx-auto"
			src={image.url}
			width={image.width}
			height={image.height}
			alt="{gameTitle} {imageTypeLabels[image.type].toLowerCase()}, full size"
		/>
	{/if}
</BaseModal>
