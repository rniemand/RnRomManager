<script lang="ts">
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import type { Me } from '../api/types';
	import Icon from './Icon.svelte';
	import ThemeToggle from './ThemeToggle.svelte';

	interface Props {
		me: Me;
		/** Pending review items (Admin); null until loaded. */
		reviewCount: number | null;
		onlogout: () => void;
		/** Opens the global search; the button is hidden while the password must be changed. */
		onsearch?: () => void;
	}

	let { me, reviewCount, onlogout, onsearch }: Props = $props();

	// Pages arrive in later steps (S41–S47); unknown ones show "Page not found" until then.
	const links = $derived(
		[
			{ href: '/', label: 'Library', icon: 'library', admin: false },
			{ href: '/ingestion', label: 'Ingestion', icon: 'ingest', admin: true },
			{ href: '/review', label: 'Review', icon: 'review', admin: true },
			{ href: '/integrity', label: 'Integrity', icon: 'integrity', admin: true },
			{ href: '/admin/users', label: 'Admin', icon: 'admin', admin: true }
		].filter((l) => !l.admin || me.roles.includes('admin'))
	);

	function isActive(href: string): boolean {
		const path = page.url.pathname;
		if (href === '/') return path === '/' || path.startsWith('/games/');
		return path === href || path.startsWith(href.split('/').slice(0, 2).join('/') + '/');
	}
</script>

<nav class="navbar navbar-expand-lg bg-body-tertiary border-bottom">
	<div class="container">
		<a class="navbar-brand d-flex align-items-center gap-2" href={resolve('/')}>
			<Icon name="system-gb" /> RnRomManager
		</a>
		<button
			class="navbar-toggler"
			type="button"
			data-bs-toggle="collapse"
			data-bs-target="#main-nav"
			aria-controls="main-nav"
			aria-expanded="false"
			aria-label="Toggle navigation"
		>
			<span class="navbar-toggler-icon"></span>
		</button>
		<div class="collapse navbar-collapse" id="main-nav">
			<ul class="navbar-nav me-auto">
				{#each links as link (link.href)}
					<li class="nav-item">
						<!-- Plain paths: most of these routes arrive in S41–S47, and resolve() only accepts existing ones. -->
						<!-- eslint-disable svelte/no-navigation-without-resolve -->
						<a
							class="nav-link d-flex align-items-center gap-1"
							class:active={isActive(link.href)}
							aria-current={isActive(link.href) ? 'page' : undefined}
							href={link.href}
						>
							<!-- eslint-enable svelte/no-navigation-without-resolve -->
							<Icon name={link.icon} />
							{link.label}
							{#if link.href === '/review' && reviewCount}
								<span class="badge rounded-pill text-bg-danger">
									{reviewCount}<span class="visually-hidden"> pending</span>
								</span>
							{/if}
						</a>
					</li>
				{/each}
			</ul>
			<ul class="navbar-nav align-items-lg-center">
				{#if onsearch && !me.mustChangePassword}
					<li class="nav-item">
						<button
							type="button"
							class="btn btn-link nav-link d-flex align-items-center gap-1"
							onclick={onsearch}
						>
							<Icon name="search" /> Search
							<kbd class="d-none d-lg-inline small ms-1" aria-hidden="true">Ctrl K</kbd>
						</button>
					</li>
				{/if}
				<li class="nav-item"><ThemeToggle /></li>
				<li class="nav-item dropdown">
					<button
						type="button"
						class="btn btn-link nav-link dropdown-toggle d-flex align-items-center gap-1"
						data-bs-toggle="dropdown"
						aria-expanded="false"
					>
						<Icon name="user" />
						{me.username}
					</button>
					<ul class="dropdown-menu dropdown-menu-end">
						<li>
							<a
								class="dropdown-item d-flex align-items-center gap-2"
								href={resolve('/change-password')}
							>
								<Icon name="password" /> Change password
							</a>
						</li>
						<li><hr class="dropdown-divider" /></li>
						<li>
							<button
								type="button"
								class="dropdown-item d-flex align-items-center gap-2"
								onclick={onlogout}
							>
								<Icon name="logout" /> Logout
							</button>
						</li>
					</ul>
				</li>
			</ul>
		</div>
	</div>
</nav>
