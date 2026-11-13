<script lang="ts">
	import 'bootstrap/dist/css/bootstrap.min.css';
	import 'bootstrap-icons/font/bootstrap-icons.min.css';
	// Font Awesome is only for gaps in Bootstrap Icons, e.g. brand logos (spec §11).
	import '@fortawesome/fontawesome-free/css/fontawesome.min.css';
	import '@fortawesome/fontawesome-free/css/brands.min.css';
	import '../app.css';
	import '#lib/bootstrap.js';
	import { onMount } from 'svelte';
	import { page } from '$app/state';
	import { api, configureClient } from '#lib/api/client.js';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import GlobalSearch from '#lib/components/GlobalSearch.svelte';
	import HotkeysModal from '#lib/components/HotkeysModal.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Navbar from '#lib/components/Navbar.svelte';
	import ToastHost from '#lib/components/ToastHost.svelte';
	import { guard } from '#lib/guard.js';
	import { navigate } from '#lib/navigation.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { hotkeys } from '#lib/stores/hotkeys.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { navCounts, refreshCounts } from '#lib/stores/navCounts.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import { theme } from '#lib/theme.svelte.js';
	import type { LayoutProps } from './$types';

	let { children }: LayoutProps = $props();

	let bootError = $state<unknown>(null);
	let searchOpen = $state(false);
	let hotkeysOpen = $state(false);

	configureClient({ navigate });

	async function boot(): Promise<void> {
		bootError = null;
		try {
			await auth.load();
		} catch (error) {
			bootError = error;
		}
	}

	onMount(() => {
		const stopTheme = theme.init();
		void boot();
		// §23, D6: the Review and Deleted ROMs badges refresh when a job ends (and on navigation, below).
		const stopJobs = jobs.onEnded(() => void refreshCounts());
		return () => {
			stopTheme();
			stopJobs();
		};
	});

	const redirect = $derived(
		auth.loaded ? guard(page.url.pathname, page.url.search, auth.me) : null
	);

	$effect(() => {
		if (redirect) void navigate(redirect);
	});

	// B24: bindings load once signed in with the password changed, and again after a password change.
	$effect(() => hotkeys.follow(auth.me));

	$effect(() => {
		void page.url.pathname; // re-run on navigation
		if (auth.isAdmin && !redirect) void refreshCounts();
	});

	async function logout(): Promise<void> {
		try {
			await api.post('/api/auth/logout');
		} catch (error) {
			toasts.show(error instanceof Error ? error.message : 'Logout failed', 'danger');
			return;
		}
		auth.set(null);
		navCounts.reset();
		await navigate('/login');
	}
</script>

<svelte:window onkeydown={(e) => hotkeys.handle(e)} />

{#if bootError}
	<main class="container py-4">
		<ErrorAlert error={bootError} onretry={boot} />
	</main>
{:else if !auth.loaded || redirect}
	<Loading />
{:else}
	{#if auth.me}
		<Navbar
			me={auth.me}
			reviewCount={navCounts.review}
			deletedCount={navCounts.deleted}
			onlogout={logout}
			onsearch={() => (searchOpen = true)}
			onhotkeys={() => (hotkeysOpen = true)}
		/>
		<GlobalSearch bind:open={searchOpen} />
		<HotkeysModal bind:open={hotkeysOpen} me={auth.me} />
	{/if}
	<main class="container py-4">
		{@render children()}
	</main>
{/if}
<ToastHost />
