# Mutants

Deliberately broken implementations used to measure how strong the simulator is (brief §5.3).
The first mutants arrive in M2: a merge that drops increments, an escrow that allows overspend,
and an LWW without a hybrid logical clock. Each must be caught by the simulator.
