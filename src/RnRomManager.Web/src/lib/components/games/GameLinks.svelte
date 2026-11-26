<script lang="ts">
	import type { GameDetail } from '#lib/types/index.js';
	import Button from '#lib/components/forms/Button.svelte';
	import { gameSearchQuery, googleSearchUrl } from '#lib/googleSearch.js';

	interface Props {
		game: GameDetail;
		/** Bound: E2 returns focus here when the Emulators modal closes. */
		emulatorsButton?: HTMLButtonElement;
		onemulators: () => void;
	}

	let { game, emulatorsButton = $bindable(), onemulators }: Props = $props();
</script>

<!-- External links: resolve() is for app routes. -->
<!-- eslint-disable svelte/no-navigation-without-resolve -->
<Button
	variant="secondary"
	outline
	size="sm"
	icon="google"
	href={googleSearchUrl(gameSearchQuery(game))}
	target="_blank"
	rel="noopener noreferrer">Google</Button
>
<Button
	variant="secondary"
	outline
	size="sm"
	icon="emulators"
	bind:ref={emulatorsButton}
	onclick={onemulators}>Emulators</Button
>
{#if game.igdbConfigured && game.igdbId !== null && game.igdbId > 0}
	<Button
		variant="secondary"
		outline
		size="sm"
		icon="external"
		href="/api/igdb/games/{game.igdbId}/page"
		target="_blank"
		rel="noopener noreferrer">View on IGDB</Button
	>
{/if}
<!-- eslint-enable svelte/no-navigation-without-resolve -->
