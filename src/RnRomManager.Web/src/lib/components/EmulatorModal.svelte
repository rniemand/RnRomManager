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
		/** All it reads (through `playUrl`), so a Library card can pass its list item's primary release (C2). */
		rom: Pick<RomDetail, 'id' | 'fileName'> | null;
		/** Sega CD B6: the release is external (a disc), so the play URL carries `disc=1`. */
		disc: boolean;
	}

	let { open = $bindable(), gameId, core, rom, disc }: Props = $props();

	let frame = $state<HTMLIFrameElement | null>(null);

	/** 3DO B11: how long the modal waits for the player's reply before closing anyway. */
	const saveTimeoutMs = 3000;

	/**
	 * 3DO B11: Opera writes its NVRAM only on reset, so the player resets and syncs /data/saves before the iframe
	 * goes. Resolves on its reply or after the timeout, whichever is first.
	 */
	function saveBeforeClose(): Promise<void> {
		const target = frame?.contentWindow;
		if (!target) return Promise.resolve();
		return new Promise((resolve) => {
			const onMessage = (event: MessageEvent) => {
				if (event.source !== target || event.origin !== location.origin) return;
				if (event.data?.type === 'rnrm-emulator-closed') done();
			};
			const done = () => {
				window.removeEventListener('message', onMessage);
				clearTimeout(timer);
				resolve();
			};
			window.addEventListener('message', onMessage);
			const timer = setTimeout(done, saveTimeoutMs);
			target.postMessage({ type: 'rnrm-emulator-close' }, location.origin);
		});
	}
</script>

<ConfirmModal
	bind:open
	title="Play {rom?.fileName ?? ''}"
	size="xl"
	beforeclose={core === 'opera' ? saveBeforeClose : undefined}
>
	<!-- Cap the width so the 4:3 frame fits the viewport height (dialog chrome ≈ 14rem): Start Game needs no scrolling. -->
	<div class="ratio ratio-4x3 mx-auto" style="max-width: calc((100vh - 14rem) * 4 / 3)">
		{#if open && rom && core}
			<iframe
				bind:this={frame}
				title="Emulator: {rom.fileName}"
				allow="fullscreen; gamepad; autoplay"
				src={playUrl(gameId, rom, core, disc)}
			></iframe>
		{/if}
	</div>
</ConfirmModal>
