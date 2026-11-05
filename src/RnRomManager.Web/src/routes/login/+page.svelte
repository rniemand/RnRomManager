<script lang="ts">
	import { page } from '$app/state';
	import { api } from '#lib/api/client.js';
	import { isApiError } from '#lib/api/errors.js';
	import type { Me } from '#lib/api/types.js';
	import Icon from '#lib/components/Icon.svelte';
	import { safeReturnUrl } from '#lib/guard.js';
	import { navigate } from '#lib/navigation.js';
	import { auth } from '#lib/stores/auth.svelte.js';

	let username = $state('');
	let password = $state('');
	let busy = $state(false);
	let error = $state<string | null>(null);

	const returnUrl = $derived(safeReturnUrl(page.url.searchParams.get('returnUrl')));

	// Already signed in (e.g. the back button): go where the visitor was heading.
	$effect(() => {
		if (auth.me) void navigate(auth.me.mustChangePassword ? '/change-password' : returnUrl);
	});

	async function submit(event: SubmitEvent): Promise<void> {
		event.preventDefault();
		if (!username || !password) {
			error = 'Enter your username and password.';
			return;
		}
		busy = true;
		error = null;
		try {
			const me = await api.post<Me>(
				'/api/auth/login',
				{ username, password },
				{ noRedirect: true }
			);
			auth.set(me);
		} catch (e) {
			password = '';
			// §9.1: one generic message whatever the reason (unknown user, wrong password, locked, disabled).
			if (isApiError(e, 'InvalidCredentials')) error = 'Invalid username or password.';
			else if (isApiError(e, 'TooManyRequests') || (isApiError(e) && e.status === 429))
				error = 'Too many sign-in attempts. Wait a minute and try again.';
			else error = isApiError(e) ? e.title : 'Sign-in failed.';
		} finally {
			busy = false;
		}
	}
</script>

<svelte:head><title>Sign in · RnRomManager</title></svelte:head>

<div class="row justify-content-center pt-sm-5">
	<div class="col-12 col-sm-8 col-md-6 col-lg-4">
		<h1 class="h3 mb-4 text-center">
			<Icon name="system-gb" /> RnRomManager
		</h1>
		<form class="card card-body" onsubmit={submit} novalidate>
			{#if error}
				<div class="alert alert-danger py-2" role="alert">{error}</div>
			{/if}
			<div class="mb-3">
				<label class="form-label" for="username">Username</label>
				<input
					id="username"
					class="form-control"
					autocomplete="username"
					autocapitalize="none"
					spellcheck="false"
					required
					bind:value={username}
				/>
			</div>
			<div class="mb-3">
				<label class="form-label" for="password">Password</label>
				<input
					id="password"
					type="password"
					class="form-control"
					autocomplete="current-password"
					required
					bind:value={password}
				/>
			</div>
			<button type="submit" class="btn btn-primary w-100" disabled={busy}>
				{#if busy}<span class="spinner-border spinner-border-sm" aria-hidden="true"></span>{/if}
				Sign in
			</button>
		</form>
	</div>
</div>
