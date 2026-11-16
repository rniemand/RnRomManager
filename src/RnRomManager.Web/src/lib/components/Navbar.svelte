<script lang="ts">
	import { resolve } from '$app/paths';
	import { page } from '$app/state';
	import type { PageRouteId } from '$app/types';
	import type { Me } from '../api/types';
	import { formatCount } from '../format';
	import Icon from './Icon.svelte';
	import { romTools } from './RomToolsTabs.svelte';
	import ThemeToggle from './ThemeToggle.svelte';

	interface Props {
		me: Me;
		/** Pending review items (Admin); null until loaded. */
		reviewCount: number | null;
		onlogout: () => void;
		/** Opens the global search; the button is hidden while the password must be changed. */
		onsearch?: () => void;
		/** Opens the keyboard shortcuts (B25); hidden while the password must be changed. */
		onhotkeys?: () => void;
		/** Bundle Creator B1: files in the working cart; the cart link shows when set and the password is changed. */
		cartCount?: number;
	}

	let { me, reviewCount, onlogout, onsearch, onhotkeys, cartCount }: Props = $props();

	// The link's name is its visible text plus hidden words ("Bundle 2 files in the cart"), so axe's
	// label-content-name-mismatch holds: visible text must be part of the accessible name.
	const cartFiles = $derived(cartCount === 1 ? 'file' : 'files');

	type NavLink = {
		href: Exclude<PageRouteId, `${string}[${string}`>;
		label: string;
		icon: string;
		admin?: boolean;
		/** Pages that also make this link active (the ROM Tools tabs). */
		section?: readonly string[];
	};

	const navigation: NavLink[] = [
		{ href: '/', label: 'Library', icon: 'library' },
		{ href: '/collections', label: 'Collections', icon: 'collections' },
		{ href: '/reports', label: 'Reports', icon: 'reports' },
		{
			href: '/ingestion',
			label: 'ROM Tools',
			icon: 'tools',
			admin: true,
			section: romTools.map((tool) => tool.href)
		},
		{ href: '/admin/users', label: 'Admin', icon: 'admin', admin: true }
	];
	const visibleNavigation = $derived(
		navigation.filter((item) => !item.admin || me.roles.includes('admin'))
	);

	function isActive(href: string): boolean {
		const path = page.url.pathname;
		if (href === '/') return path === '/' || path.startsWith('/games/');
		return path === href || path.startsWith(href.split('/').slice(0, 2).join('/') + '/');
	}
</script>

<!-- B26: sticky; modals (1055) and dropdowns still render above it (sticky-top is 1020). -->
<nav class="navbar navbar-expand-lg bg-body-tertiary border-bottom sticky-top">
	<div class="container">
		<a
			class="navbar-brand d-flex align-items-center"
			href={resolve('/')}
			aria-label="RnRomManager home"
		>
			<Icon name="system-gb" />
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
				{#each visibleNavigation as item (item.label)}
					<li class="nav-item">{@render navigationLink(item)}</li>
				{/each}
			</ul>
			<ul class="navbar-nav align-items-lg-center navigation-utilities">
				{#if cartCount !== undefined && !me.mustChangePassword}
					<li class="nav-item">
						<a
							class="nav-link d-flex align-items-center gap-1"
							class:active={isActive('/bundle-creator')}
							aria-current={isActive('/bundle-creator') ? 'page' : undefined}
							href={resolve('/bundle-creator')}
						>
							<!-- eslint-disable-next-line svelte/no-useless-mustaches -- Svelte trims a literal space at the start of a block -->
							<Icon name="cart" /> Bundle{#if cartCount}{' '}
								<span class="badge rounded-pill text-bg-primary">{formatCount(cartCount)}</span>
								<span class="visually-hidden">{cartFiles} in the cart</span>
							{/if}
						</a>
					</li>
				{/if}
				{#if onsearch && !me.mustChangePassword}
					<li class="nav-item">
						<button
							type="button"
							class="btn btn-link nav-link d-flex align-items-center gap-1"
							onclick={onsearch}
						>
							<Icon name="search" /> Search
						</button>
					</li>
				{/if}
				{#if onhotkeys && !me.mustChangePassword}
					<li class="nav-item">
						<button
							type="button"
							class="btn btn-link nav-link d-flex align-items-center"
							aria-label="Keyboard shortcuts"
							title="Keyboard shortcuts"
							onclick={onhotkeys}
						>
							<Icon name="help" />
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

{#snippet navigationLink(link: NavLink)}
	{@const active = link.section ? link.section.some(isActive) : isActive(link.href)}
	<a
		class="nav-link d-flex align-items-center gap-1"
		class:active
		aria-current={active ? 'page' : undefined}
		href={resolve(link.href)}
	>
		<Icon name={link.icon} />
		{link.label}
		{#if link.section?.includes('/review') && reviewCount}
			<span class="badge rounded-pill text-bg-danger">
				{formatCount(reviewCount)}<span class="visually-hidden">&#32;pending review</span>
			</span>
		{/if}
	</a>
{/snippet}

<style>
	@media (max-width: 991.98px) {
		.navbar-collapse:global(.show) {
			max-height: calc(100dvh - 4rem);
			overflow-y: auto;
		}

		.navigation-utilities {
			margin-top: 0.5rem;
			padding-top: 0.5rem;
			border-top: 1px solid var(--bs-border-color);
		}
	}
</style>
