<script lang="ts">
	import { onMount } from 'svelte';
	import { api } from '../api/client';
	import type { About, Me } from '../api/types';
	import AboutModal from './AboutModal.svelte';
	import Icon from './Icon.svelte';

	/**
	 * B10–B12: the signed-in footer, fixed to the bottom: About, the version (a link to the releases) and the keyboard
	 * shortcuts button (hidden while the password must be changed, like the rest of the app).
	 */
	interface Props {
		me: Me;
		/** Opens the keyboard shortcuts (B25). */
		onhotkeys: () => void;
	}

	let { me, onhotkeys }: Props = $props();

	const releasesUrl = 'https://github.com/rniemand/RnRomManager/releases';
	let about = $state<About | null>(null);
	let error = $state<unknown>(null);
	let aboutOpen = $state(false);

	// B11: once after sign-in; /api/about also answers while the password must be changed.
	async function load(): Promise<void> {
		error = null;
		try {
			about = await api.get<About>('/api/about');
		} catch (e) {
			error = e;
		}
	}

	onMount(() => void load());
</script>

<footer class="app-footer fixed-bottom bg-body-tertiary border-top small">
	<div class="container d-flex align-items-center gap-3 py-1">
		<button
			type="button"
			class="btn btn-link btn-sm p-0 link-body-emphasis text-decoration-none"
			onclick={() => (aboutOpen = true)}><Icon name="info" /> About</button
		>
		<a
			class="link-body-emphasis text-decoration-none"
			href={releasesUrl}
			target="_blank"
			rel="noopener noreferrer">RnRomManager{about ? ` ${about.version}` : ''}</a
		>
		{#if !me.mustChangePassword}
			<button
				type="button"
				class="btn btn-link btn-sm p-0 ms-auto link-body-emphasis"
				aria-label="Keyboard shortcuts"
				title="Keyboard shortcuts"
				onclick={onhotkeys}><Icon name="help" /></button
			>
		{/if}
	</div>
</footer>

<AboutModal bind:open={aboutOpen} {about} {error} onretry={load} />
