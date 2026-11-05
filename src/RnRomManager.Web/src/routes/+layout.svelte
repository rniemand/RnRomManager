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
	import type { Page } from '#lib/api/types.js';
	import ErrorAlert from '#lib/components/ErrorAlert.svelte';
	import Loading from '#lib/components/Loading.svelte';
	import Navbar from '#lib/components/Navbar.svelte';
	import ToastHost from '#lib/components/ToastHost.svelte';
	import { guard } from '#lib/guard.js';
	import { navigate } from '#lib/navigation.js';
	import { auth } from '#lib/stores/auth.svelte.js';
	import { jobs } from '#lib/stores/jobs.svelte.js';
	import { toasts } from '#lib/stores/toasts.svelte.js';
	import { theme } from '#lib/theme.svelte.js';
	import type { LayoutProps } from './$types';

	let { children }: LayoutProps = $props();

	let bootError = $state<unknown>(null);
	let reviewCount = $state<number | null>(null);

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
		// §23: the Review badge refreshes when a job ends (and on navigation, below).
		const stopJobs = jobs.onEnded(() => void loadReviewCount());
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

	async function loadReviewCount(): Promise<void> {
		// Every /api call but me/change-password/logout is 403 until the password is changed (§9.1).
		if (!auth.isAdmin || auth.me?.mustChangePassword) return;
		try {
			reviewCount = (await api.get<Page<unknown>>('/api/review?status=Pending&pageSize=1')).total;
		} catch {
			// the badge is a hint; leave the last value
		}
	}

	$effect(() => {
		void page.url.pathname; // re-run on navigation
		if (auth.isAdmin && !redirect) void loadReviewCount();
	});

	async function logout(): Promise<void> {
		try {
			await api.post('/api/auth/logout');
		} catch (error) {
			toasts.show(error instanceof Error ? error.message : 'Logout failed', 'danger');
			return;
		}
		auth.set(null);
		reviewCount = null;
		await navigate('/login');
	}
</script>

{#if bootError}
	<main class="container py-4">
		<ErrorAlert error={bootError} onretry={boot} />
	</main>
{:else if !auth.loaded || redirect}
	<Loading />
{:else}
	{#if auth.me}
		<Navbar me={auth.me} {reviewCount} onlogout={logout} />
	{/if}
	<main class="container py-4">
		{@render children()}
	</main>
{/if}
<ToastHost />
