---
title: Présentation d'Andy Policies
slug: andy-policies-overview
order: 1
tags: [policies, governance, compliance]
---

# Présentation d'Andy Policies

Andy Policies est le catalogue de politiques de gouvernance de l'écosystème Andy. Il stocke des documents de politique structurés et versionnés avec un cycle de vie et un journal d'audit. Conductor consomme ces politiques pour l'admission des stories, la vérification des exécutions d'agents et le rapport de conformité.

## Ce qu'il fait

- Stocke les politiques avec des champs structurés (id, version, statut, applies-to, content, effective-from).
- Suit le cycle de vie des politiques : `draft → active → winding-down → retired`.
- Enregistre chaque lecture et chaque transition de statut dans un journal d'audit retenu selon le réglage de rétention de l'organisation.
- Sert les politiques aux consommateurs (Conductor, agents) via une API HTTP versionnée ; les consommateurs mettent en cache par version.
- Expose une vue de diff entre versions de politique pour révision.

## Concepts clés

- **Document de politique** — l'unité. Possède des métadonnées, du contenu et une version immuable.
- **Applies-to** — la portée que la politique couvre (`agent-run`, `repo-write`, `data-export`, …). Les consommateurs interrogent par cela.
- **Application vs contenu** — Policies stocke le *contenu* ; l'application (la porte réelle) réside dans le service consommateur. Ce service est la source de vérité, pas le videur.

## Où il s'intègre

Les chemins Tasks/Agents/Docs de Conductor lisent les politiques aux bons moments pré-action. Dépend d'Auth, RBAC et Settings. La plupart des données de politique sont en lecture majoritaire ; les écritures sont rares (admin seulement).

## Configuration

La rétention de politique, la rétention du journal d'audit et les bascules de révision requise résident sous `andy.policies.*` dans `andy-settings`. La graine de catalogue (politiques par défaut pour une installation fraîche) réside dans `config/registration.json`. Conductor expose le catalogue en direct dans **Policies** (onglet de premier niveau).

## Dépannage

- **Une politique n'est pas appliquée** — vérifiez que le service consommateur interroge la bonne portée `applies-to` et vérifiez que sa version en cache est à jour.
- **Édition bloquée avec « doit passer par révision »** — la politique est en `active`. Publiez une nouvelle version (la version `Active` précédente passe automatiquement en `WindingDown`).
- **Lacunes d'audit** — Policies enregistre chaque accès ; les lacunes signifient habituellement que l'abonné d'audit a perdu sa connexion NATS. Redémarrez le consommateur.
