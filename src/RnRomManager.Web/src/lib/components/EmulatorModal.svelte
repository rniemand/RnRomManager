<script lang="ts">
	import type { RomDetail } from '../api/types';
	import { playUrl } from '../emulator';
	import ConfirmModal from './ConfirmModal.svelte';

	/**
	 * Browser emulators B4: the play page in a same-origin iframe. The iframe exists only while open, so closing
	 * the modal stops the emulator and its audio.
	 */
	interface Props {
		open: boolean;
		gameId: string;
		core: string | null;
		rom: RomDetail | null;
	}

	let { open = $bindable(), gameId, core, rom }: Props = $props();
</script>

<ConfirmModal bind:open title="Play {rom?.fileName ?? ''}" size="xl">
	<!-- Cap the width so the 4:3 frame fits the viewport height (dialog chrome ≈ 14rem): Start Game needs no scrolling. -->
	<div class="ratio ratio-4x3 mx-auto" style="max-width: calc((100vh - 14rem) * 4 / 3)">
		{#if open && rom && core}
			<iframe
				title="Emulator: {rom.fileName}"
				allow="fullscreen; gamepad; autoplay"
				src={playUrl(gameId, rom, core)}
			></iframe>
		{/if}
	</div>
</ConfirmModal>
